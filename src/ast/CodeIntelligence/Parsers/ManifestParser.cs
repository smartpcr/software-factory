// -----------------------------------------------------------------------
// <copyright file="ManifestParser.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.Parsers;

using System.Xml.Linq;
using System.Xml.XPath;

/// <summary>
/// Parser for service manifests including WinApplicationManifest.xml,
/// ServiceManifest.xml (Service Fabric), and Settings.xml.
/// </summary>
public sealed class ManifestParser
{
    private static readonly EnumerationOptions SafeEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
    };

    /// <summary>
    /// Parses a WinApplicationManifest.xml file for service definitions.
    /// </summary>
    /// <param name="filePath">The path to the manifest file.</param>
    /// <returns>List of service definitions.</returns>
    public async Task<IReadOnlyList<ServiceDefinition>> ParseWinApplicationManifestAsync(string filePath)
    {
        var services = new List<ServiceDefinition>();

        try
        {
            var content = await File.ReadAllTextAsync(filePath);
            var doc = XDocument.Parse(content);

            // Try to find services
            var serviceElements = doc.Descendants()
                .Where(e => e.Name.LocalName == "Service" ||
                           e.Name.LocalName == "ServiceType");

            foreach (var element in serviceElements)
            {
                var service = this.ParseServiceElement(element, filePath);
                if (service != null)
                {
                    services.Add(service);
                }
            }

            // Also look for AssemblyFile references
            var assemblyElements = doc.Descendants()
                .Where(e => e.Name.LocalName == "AssemblyFile" ||
                           e.Attributes().Any(a => a.Name.LocalName == "AssemblyFile"));

            foreach (var element in assemblyElements)
            {
                var assemblyFile = element.Value;
                if (string.IsNullOrEmpty(assemblyFile))
                {
                    assemblyFile = element.Attribute("AssemblyFile")?.Value ??
                                    element.Attribute("assemblyFile")?.Value;
                }

                if (!string.IsNullOrEmpty(assemblyFile))
                {
                    services.Add(new ServiceDefinition
                    {
                        ManifestType = ManifestType.WinApplication,
                        AssemblyFile = assemblyFile,
                        FilePath = filePath,
                        LineNumber = GetLineNumber(element)
                    });
                }
            }
        }
        catch (Exception)
        {
            // Skip files that can't be parsed
        }

        return services;
    }

    /// <summary>
    /// Parses a ServiceManifest.xml file (Service Fabric) for service definitions.
    /// </summary>
    /// <param name="filePath">The path to the manifest file.</param>
    /// <returns>List of service definitions.</returns>
    public async Task<IReadOnlyList<ServiceDefinition>> ParseServiceFabricManifestAsync(string filePath)
    {
        var services = new List<ServiceDefinition>();

        try
        {
            var content = await File.ReadAllTextAsync(filePath);
            var doc = XDocument.Parse(content);

            // Service Fabric namespace
            XNamespace sf = "http://schemas.microsoft.com/2011/01/fabric";

            // Find ServiceManifest root
            var manifest = doc.Root;
            if (manifest?.Name.LocalName != "ServiceManifest")
            {
                return services;
            }

            var serviceName = manifest.Attribute("Name")?.Value;
            var serviceVersion = manifest.Attribute("Version")?.Value;

            // Find CodePackage for entry point
            var codePackages = manifest.Descendants()
                .Where(e => e.Name.LocalName == "CodePackage");

            foreach (var codePackage in codePackages)
            {
                var packageName = codePackage.Attribute("Name")?.Value;

                // Find EntryPoint
                var entryPoint = codePackage.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "EntryPoint");

                var exeHost = entryPoint?.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "ExeHost");

                var program = exeHost?.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "Program");

                var assemblyFile = program?.Value;

                // Find service types
                var serviceTypes = manifest.Descendants()
                    .Where(e => e.Name.LocalName.EndsWith("ServiceType"));

                foreach (var serviceType in serviceTypes)
                {
                    var typeName = serviceType.Attribute("ServiceTypeName")?.Value;
                    var isStateless = serviceType.Name.LocalName.Contains("Stateless");

                    services.Add(new ServiceDefinition
                    {
                        ManifestType = ManifestType.ServiceFabric,
                        ServiceName = serviceName,
                        ServiceTypeName = typeName,
                        AssemblyFile = assemblyFile,
                        Version = serviceVersion,
                        IsStateless = isStateless,
                        CodePackageName = packageName,
                        FilePath = filePath,
                        LineNumber = GetLineNumber(serviceType)
                    });
                }
            }
        }
        catch (Exception)
        {
            // Skip files that can't be parsed
        }

        return services;
    }

    /// <summary>
    /// Parses a Settings.xml file for host configuration.
    /// </summary>
    /// <param name="filePath">The path to the settings file.</param>
    /// <returns>List of host settings.</returns>
    public async Task<IReadOnlyList<HostSetting>> ParseSettingsXmlAsync(string filePath)
    {
        var settings = new List<HostSetting>();

        try
        {
            var content = await File.ReadAllTextAsync(filePath);
            var doc = XDocument.Parse(content);

            // Find all Section elements
            var sections = doc.Descendants()
                .Where(e => e.Name.LocalName == "Section");

            foreach (var section in sections)
            {
                var sectionName = section.Attribute("Name")?.Value;

                // Find all Parameter elements within section
                var parameters = section.Descendants()
                    .Where(e => e.Name.LocalName == "Parameter");

                foreach (var param in parameters)
                {
                    var paramName = param.Attribute("Name")?.Value;
                    var paramValue = param.Attribute("Value")?.Value;

                    if (!string.IsNullOrEmpty(paramName))
                    {
                        settings.Add(new HostSetting
                        {
                            SectionName = sectionName,
                            ParameterName = paramName,
                            Value = paramValue,
                            IsServiceAssembly = paramName.Equals("ServiceAssembly", StringComparison.OrdinalIgnoreCase),
                            FilePath = filePath,
                            LineNumber = GetLineNumber(param)
                        });
                    }
                }
            }
        }
        catch (Exception)
        {
            // Skip files that can't be parsed
        }

        return settings;
    }

    /// <summary>
    /// Parses a .csproj file for SDK type and output type.
    /// </summary>
    /// <param name="filePath">The path to the .csproj file.</param>
    /// <returns>Project SDK information.</returns>
    public async Task<ProjectSdkInfo?> ParseProjectSdkAsync(string filePath)
    {
        try
        {
            var content = await File.ReadAllTextAsync(filePath);
            var doc = XDocument.Parse(content);

            var root = doc.Root;
            if (root == null || root.Name.LocalName != "Project")
            {
                return null;
            }

            var sdk = root.Attribute("Sdk")?.Value;
            var outputType = root.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "OutputType")?.Value;
            var targetFramework = root.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "TargetFramework" ||
                                    e.Name.LocalName == "TargetFrameworks")?.Value;

            // Determine project type from SDK
            var projectType = DetermineProjectType(sdk, outputType);

            return new ProjectSdkInfo
            {
                Sdk = sdk,
                OutputType = outputType,
                TargetFramework = targetFramework,
                ProjectType = projectType,
                FilePath = filePath,
                IsExecutable = outputType?.Equals("Exe", StringComparison.OrdinalIgnoreCase) == true ||
                              outputType?.Equals("WinExe", StringComparison.OrdinalIgnoreCase) == true,
                IsWebProject = sdk?.Contains("Web") == true,
                IsWorkerProject = sdk?.Contains("Worker") == true
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds manifests associated with a project by scanning the .csproj for
    /// Content/None items that are copied to output (CopyToOutputDirectory).
    /// </summary>
    /// <param name="csprojPath">Path to the .csproj file.</param>
    /// <returns>List of associated manifest files found in source.</returns>
    public async Task<IReadOnlyList<ProjectManifestReference>> FindProjectManifestsAsync(string csprojPath)
    {
        var manifests = new List<ProjectManifestReference>();

        try
        {
            var content = await File.ReadAllTextAsync(csprojPath);
            var doc = XDocument.Parse(content);
            var projectDir = Path.GetDirectoryName(csprojPath) ?? "";

            // Find Content and None items with CopyToOutputDirectory
            var items = doc.Descendants()
                .Where(e => (e.Name.LocalName == "Content" || e.Name.LocalName == "None") &&
                           e.Descendants().Any(d => d.Name.LocalName == "CopyToOutputDirectory"));

            foreach (var item in items)
            {
                var include = item.Attribute("Include")?.Value;
                if (string.IsNullOrEmpty(include))
                {
                    continue;
                }

                // Resolve relative path
                var relativePath = include.TrimStart('.', '\\', '/');
                var absolutePath = Path.GetFullPath(Path.Combine(projectDir, relativePath));

                if (!File.Exists(absolutePath))
                {
                    continue;
                }

                var copyAction = item.Descendants()
                    .FirstOrDefault(d => d.Name.LocalName == "CopyToOutputDirectory")?.Value;

                var manifestType = DetectManifestTypeFromFileName(absolutePath);
                if (manifestType != ManifestType.Unknown)
                {
                    manifests.Add(new ProjectManifestReference
                    {
                        ProjectPath = csprojPath,
                        ManifestPath = absolutePath,
                        RelativePath = include,
                        ManifestType = manifestType,
                        CopyAction = copyAction ?? "PreserveNewest",
                        ItemType = item.Name.LocalName
                    });
                }
            }
        }
        catch (Exception)
        {
            // Skip files that can't be parsed
        }

        return manifests;
    }

    /// <summary>
    /// Scans a directory for manifest files that might indicate entry points.
    /// </summary>
    /// <param name="directory">Directory to scan.</param>
    /// <param name="searchPattern">Optional search pattern.</param>
    /// <returns>List of found manifest paths.</returns>
    public IReadOnlyList<string> FindManifestFiles(string directory, string? searchPattern = null)
    {
        var manifests = new List<string>();
        var patterns = new[]
        {
            "WinApplicationManifest.xml",
            "ServiceManifest.xml",
            "ApplicationManifest.xml",
            "Settings.xml"
        };

        try
        {
            foreach (var pattern in patterns)
            {
                if (searchPattern != null && !pattern.Contains(searchPattern, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var files = Directory.GetFiles(directory, pattern, SafeEnumerationOptions);
                manifests.AddRange(files);
            }
        }
        catch (Exception)
        {
            // Skip directories that can't be accessed
        }

        return manifests;
    }

    /// <summary>
    /// Gets manifest files associated with an entry point project.
    /// Searches the project directory and parses .csproj for manifest references.
    /// </summary>
    /// <param name="entryPointPath">Path to the entry point source file.</param>
    /// <returns>List of associated manifests with their content.</returns>
    public async Task<IReadOnlyList<ManifestInfo>> GetEntryPointManifestsAsync(string entryPointPath)
    {
        var results = new List<ManifestInfo>();
        var directory = Path.GetDirectoryName(entryPointPath);

        if (string.IsNullOrEmpty(directory))
        {
            return results;
        }

        // Find .csproj in same directory or parent
        var csprojFiles = FindProjectFile(directory);

        foreach (var csproj in csprojFiles)
        {
            // Get manifests referenced in the project
            var manifestRefs = await this.FindProjectManifestsAsync(csproj);

            foreach (var manifestRef in manifestRefs)
            {
                var manifestInfo = await this.ParseManifestAsync(manifestRef.ManifestPath);
                if (manifestInfo != null)
                {
                    results.Add(manifestInfo);
                }
            }
        }

        // Also check for manifests in the same directory
        var localManifests = this.FindManifestFiles(directory);
        foreach (var manifestPath in localManifests.Where(m => !results.Any(r => r.FilePath == m)))
        {
            var manifestInfo = await this.ParseManifestAsync(manifestPath);
            if (manifestInfo != null)
            {
                results.Add(manifestInfo);
            }
        }

        return results;
    }

    private static IEnumerable<string> FindProjectFile(string directory)
    {
        var current = directory;
        while (!string.IsNullOrEmpty(current))
        {
            var csprojFiles = Directory.GetFiles(current, "*.csproj");
            if (csprojFiles.Length > 0)
            {
                return csprojFiles;
            }

            current = Path.GetDirectoryName(current);
        }

        return [];
    }

    private static ManifestType DetectManifestTypeFromFileName(string filePath)
    {
        var fileName = Path.GetFileName(filePath).ToLowerInvariant();

        if (fileName.Contains("winapplicationmanifest"))
        {
            return ManifestType.WinApplication;
        }

        if (fileName.Contains("servicemanifest") || fileName.Contains("applicationmanifest"))
        {
            return ManifestType.ServiceFabric;
        }

        if (fileName == "settings.xml")
        {
            return ManifestType.Settings;
        }

        return ManifestType.Unknown;
    }

    /// <summary>
    /// Auto-detects manifest type and parses accordingly.
    /// </summary>
    /// <param name="filePath">The path to the manifest file.</param>
    /// <returns>Detected manifest information.</returns>
    public async Task<ManifestInfo?> ParseManifestAsync(string filePath)
    {
        var fileName = Path.GetFileName(filePath).ToLowerInvariant();

        if (fileName.Contains("winapplicationmanifest"))
        {
            var services = await this.ParseWinApplicationManifestAsync(filePath);
            return new ManifestInfo
            {
                Type = ManifestType.WinApplication,
                FilePath = filePath,
                Services = services
            };
        }

        if (fileName.Contains("servicemanifest"))
        {
            var services = await this.ParseServiceFabricManifestAsync(filePath);
            return new ManifestInfo
            {
                Type = ManifestType.ServiceFabric,
                FilePath = filePath,
                Services = services
            };
        }

        if (fileName == "settings.xml")
        {
            var settings = await this.ParseSettingsXmlAsync(filePath);
            return new ManifestInfo
            {
                Type = ManifestType.Settings,
                FilePath = filePath,
                HostSettings = settings
            };
        }

        if (fileName.EndsWith(".csproj"))
        {
            var sdkInfo = await this.ParseProjectSdkAsync(filePath);
            if (sdkInfo != null)
            {
                return new ManifestInfo
                {
                    Type = ManifestType.Project,
                    FilePath = filePath,
                    ProjectSdk = sdkInfo
                };
            }
        }

        return null;
    }

    private ServiceDefinition? ParseServiceElement(XElement element, string filePath)
    {
        var serviceName = element.Attribute("Name")?.Value ??
                          element.Attribute("name")?.Value ??
                          element.Attribute("ServiceTypeName")?.Value;

        var assemblyFile = element.Attribute("AssemblyFile")?.Value ??
                           element.Descendants().FirstOrDefault(e => e.Name.LocalName == "AssemblyFile")?.Value;

        if (string.IsNullOrEmpty(serviceName) && string.IsNullOrEmpty(assemblyFile))
        {
            return null;
        }

        return new ServiceDefinition
        {
            ManifestType = ManifestType.WinApplication,
            ServiceName = serviceName,
            AssemblyFile = assemblyFile,
            FilePath = filePath,
            LineNumber = GetLineNumber(element)
        };
    }

    private static ProjectType DetermineProjectType(string? sdk, string? outputType)
    {
        if (sdk == null)
        {
            return ProjectType.Unknown;
        }

        if (sdk.Contains("Web"))
        {
            return ProjectType.AspNetCore;
        }

        if (sdk.Contains("Worker"))
        {
            return ProjectType.Worker;
        }

        if (sdk.Contains("BlazorWebAssembly"))
        {
            return ProjectType.BlazorWasm;
        }

        if (sdk.Contains("Razor"))
        {
            return ProjectType.RazorLibrary;
        }

        if (outputType?.Equals("Exe", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ProjectType.Console;
        }

        if (outputType?.Equals("Library", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ProjectType.Library;
        }

        return ProjectType.Unknown;
    }

    private static int GetLineNumber(XElement element)
    {
        var lineInfo = element as System.Xml.IXmlLineInfo;
        return lineInfo?.HasLineInfo() == true ? lineInfo.LineNumber : 1;
    }
}

/// <summary>
/// Information about a service definition from a manifest.
/// </summary>
public record ServiceDefinition
{
    /// <summary>Gets the manifest type.</summary>
    public required ManifestType ManifestType { get; init; }

    /// <summary>Gets the service name.</summary>
    public string? ServiceName { get; init; }

    /// <summary>Gets the service type name.</summary>
    public string? ServiceTypeName { get; init; }

    /// <summary>Gets the assembly file.</summary>
    public string? AssemblyFile { get; init; }

    /// <summary>Gets the version.</summary>
    public string? Version { get; init; }

    /// <summary>Gets whether the service is stateless.</summary>
    public bool IsStateless { get; init; }

    /// <summary>Gets the code package name.</summary>
    public string? CodePackageName { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }
}

/// <summary>
/// Information about a host setting.
/// </summary>
public record HostSetting
{
    /// <summary>Gets the section name.</summary>
    public string? SectionName { get; init; }

    /// <summary>Gets the parameter name.</summary>
    public required string ParameterName { get; init; }

    /// <summary>Gets the parameter value.</summary>
    public string? Value { get; init; }

    /// <summary>Gets whether this is a ServiceAssembly parameter.</summary>
    public bool IsServiceAssembly { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the line number.</summary>
    public required int LineNumber { get; init; }
}

/// <summary>
/// Information about a project SDK.
/// </summary>
public record ProjectSdkInfo
{
    /// <summary>Gets the SDK name.</summary>
    public string? Sdk { get; init; }

    /// <summary>Gets the output type.</summary>
    public string? OutputType { get; init; }

    /// <summary>Gets the target framework.</summary>
    public string? TargetFramework { get; init; }

    /// <summary>Gets the project type.</summary>
    public required ProjectType ProjectType { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets whether this is an executable project.</summary>
    public bool IsExecutable { get; init; }

    /// <summary>Gets whether this is a web project.</summary>
    public bool IsWebProject { get; init; }

    /// <summary>Gets whether this is a worker project.</summary>
    public bool IsWorkerProject { get; init; }
}

/// <summary>
/// Combined manifest information.
/// </summary>
public record ManifestInfo
{
    /// <summary>Gets the manifest type.</summary>
    public required ManifestType Type { get; init; }

    /// <summary>Gets the file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets the services defined in the manifest.</summary>
    public IReadOnlyList<ServiceDefinition> Services { get; init; } = [];

    /// <summary>Gets the host settings.</summary>
    public IReadOnlyList<HostSetting> HostSettings { get; init; } = [];

    /// <summary>Gets the project SDK info.</summary>
    public ProjectSdkInfo? ProjectSdk { get; init; }
}

/// <summary>
/// Types of manifests.
/// </summary>
public enum ManifestType
{
    /// <summary>Unknown manifest type.</summary>
    Unknown,

    /// <summary>WinApplicationManifest.xml.</summary>
    WinApplication,

    /// <summary>Service Fabric ServiceManifest.xml.</summary>
    ServiceFabric,

    /// <summary>Settings.xml.</summary>
    Settings,

    /// <summary>.csproj project file.</summary>
    Project
}

/// <summary>
/// Types of projects based on SDK.
/// </summary>
public enum ProjectType
{
    /// <summary>Unknown project type.</summary>
    Unknown,

    /// <summary>Console application.</summary>
    Console,

    /// <summary>Class library.</summary>
    Library,

    /// <summary>ASP.NET Core web application.</summary>
    AspNetCore,

    /// <summary>Worker service.</summary>
    Worker,

    /// <summary>Blazor WebAssembly.</summary>
    BlazorWasm,

    /// <summary>Razor class library.</summary>
    RazorLibrary
}

/// <summary>
/// Reference to a manifest file from a .csproj.
/// </summary>
public record ProjectManifestReference
{
    /// <summary>Gets the project file path.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>Gets the absolute path to the manifest.</summary>
    public required string ManifestPath { get; init; }

    /// <summary>Gets the relative path as specified in the project.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Gets the manifest type.</summary>
    public required ManifestType ManifestType { get; init; }

    /// <summary>Gets the copy action (PreserveNewest, Always, Never).</summary>
    public required string CopyAction { get; init; }

    /// <summary>Gets the item type (Content, None).</summary>
    public required string ItemType { get; init; }
}
