// -----------------------------------------------------------------------
// <copyright file="IncidentPromptHelper.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace CopilotChat.Helpers;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Loads prompt templates from the Prompts folder and resolves placeholders
/// to generate ready-to-send prompts for each investigation step.
/// </summary>
public class IncidentPromptHelper
{
    private const string PromptsFolder = "Prompts";
    private const string RepoMappingFile = @"E:\work\hub\docs\wiki\repos.md";

    private readonly string incidentFolder;
    private readonly Dictionary<string, string> repoMappings;
    private readonly ILogger<IncidentPromptHelper> logger;
    private readonly string promptsBasePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="IncidentPromptHelper"/> class.
    /// </summary>
    /// <param name="incidentFolder">Path to the incident output folder.</param>
    /// <param name="logger">Logger instance.</param>
    public IncidentPromptHelper(string incidentFolder, ILogger<IncidentPromptHelper> logger)
    {
        this.incidentFolder = incidentFolder ?? throw new ArgumentNullException(nameof(incidentFolder));
        this.repoMappings = this.ParseRepoMapping();
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.promptsBasePath = Path.Combine(AppContext.BaseDirectory, PromptsFolder);
    }

    /// <summary>
    /// Loads a prompt template by name (filename without extension) from the Prompts folder.
    /// </summary>
    /// <param name="promptName">The prompt name (e.g., "identify-issues").</param>
    /// <returns>The raw template text, or null if not found.</returns>
    public string? LoadTemplate(string promptName)
    {
        var filePath = Path.Combine(this.promptsBasePath, $"{promptName}.md");
        if (!File.Exists(filePath))
        {
            this.logger.LogWarning("Prompt template not found: {PromptName} at {Path}", promptName, filePath);
            return null;
        }

        this.logger.LogDebug("Loaded prompt template: {PromptName}", promptName);
        return File.ReadAllText(filePath);
    }

    /// <summary>
    /// Lists all available prompt template names.
    /// </summary>
    /// <returns>Collection of prompt names (without extension).</returns>
    public IReadOnlyList<string> ListPrompts()
    {
        if (!Directory.Exists(this.promptsBasePath))
        {
            this.logger.LogWarning("Prompts folder not found: {Path}", this.promptsBasePath);
            return [];
        }

        var names = Directory.GetFiles(this.promptsBasePath, "*.md")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n)
            .ToList();

        this.logger.LogDebug("Found {Count} prompt templates", names.Count);
        return names;
    }

    /// <summary>
    /// Resolves placeholders in a template string. Placeholders use <c>{key}</c> syntax.
    /// </summary>
    /// <param name="template">The template with placeholders.</param>
    /// <param name="parameters">Key-value pairs to substitute.</param>
    /// <returns>The resolved prompt string.</returns>
    public string ResolveTemplate(string template, Dictionary<string, string> parameters)
    {
        var result = template;
        foreach (var kvp in parameters)
        {
            var placeholder = $"{{{kvp.Key}}}";
            if (result.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
            {
                result = result.Replace(placeholder, kvp.Value);
                this.logger.LogDebug("Resolved placeholder {Key}", kvp.Key);
            }
        }

        // Warn about unresolved placeholders
        var unresolved = Regex.Matches(result, @"\{(\w+)\}");
        foreach (Match match in unresolved)
        {
            this.logger.LogWarning("Unresolved placeholder: {Placeholder}", match.Value);
        }

        return result;
    }

    /// <summary>
    /// Generates the identify-issues prompt.
    /// </summary>
    /// <returns>The resolved prompt string.</returns>
    public string GenerateIdentifyIssuesPrompt()
    {
        this.logger.LogInformation("Generating identify-issues prompt for folder: {Folder}", this.incidentFolder);

        return this.BuildPrompt("identify-issues", new Dictionary<string, string>
        {
            ["incident_folder"] = this.incidentFolder
        });
    }

    /// <summary>
    /// Generates the repo-identifier prompt.
    /// </summary>
    /// <returns>The resolved prompt string.</returns>
    public string GenerateRepoIdentifierPrompt()
    {
        var issuesFile = Path.Combine(this.incidentFolder, "issues_list.md");
        this.logger.LogInformation("Generating repo-identifier prompt");

        return this.BuildPrompt("repo-identifier", new Dictionary<string, string>
        {
            ["issues_file"] = issuesFile,
            ["repos_file"] = @"E:\work\hub\docs\wiki\repos.md",
            ["output_file"] = Path.Combine(this.incidentFolder, "repo_identified.md")
        });
    }

    /// <summary>
    /// Generates the code-explorer prompt.
    /// Reads repo paths from repo_identified.md in the incident folder.
    /// </summary>
    /// <returns>The resolved prompt string.</returns>
    public string GenerateCodeExplorerPrompt()
    {
        var repoPaths = this.ParseRepoPaths();
        var repoList = string.Join(", ", repoPaths);
        var issuesFile = Path.Combine(this.incidentFolder, "issues_list.md");
        this.logger.LogInformation("Generating code-explorer prompt for repos: {Repos}", repoList);

        return this.BuildPrompt("code-explorer", new Dictionary<string, string>
        {
            ["repo_list"] = repoList,
            ["issues_file"] = issuesFile,
            ["output_file"] = Path.Combine(this.incidentFolder, "code_issues.md")
        });
    }

    /// <summary>
    /// Generates the find-etw-logs prompt.
    /// Reads the primary repo path from repo_identified.md in the incident folder.
    /// </summary>
    /// <returns>The resolved prompt string.</returns>
    public string GenerateFindEtwLogsPrompt()
    {
        var repoPaths = this.ParseRepoPaths();
        var primaryRepoPath = repoPaths.FirstOrDefault()
            ?? throw new InvalidOperationException("No repo paths found in repo_identified.md");
        var codeIssuesFile = Path.Combine(this.incidentFolder, "code_issues.md");
        this.logger.LogInformation("Generating find-etw-logs prompt for repo: {Repo}", primaryRepoPath);

        return this.BuildPrompt("find-etw-logs", new Dictionary<string, string>
        {
            ["code_issues_file"] = codeIssuesFile,
            ["repo_path"] = primaryRepoPath,
            ["output_file"] = Path.Combine(this.incidentFolder, "etw_events.md")
        });
    }

    /// <summary>
    /// Generates the summarize-incident prompt.
    /// </summary>
    /// <param name="maxContentLength">Maximum character length for incident content.</param>
    /// <returns>The resolved prompt string.</returns>
    public string GenerateSummarizeIncidentPrompt(int maxContentLength = 8000)
    {
        var content = this.ReadAndCombineFiles(
            new[] { "metadata.md", "summary.md", "discussion.md", "rca.md" },
            maxContentLength);

        this.logger.LogInformation("Generating summarize-incident prompt ({Length} chars)", content.Length);

        return this.BuildPrompt("summarize-incident", new Dictionary<string, string>
        {
            ["incident_content"] = content
        });
    }

    /// <summary>
    /// Generates the analyze-evidence prompt.
    /// </summary>
    /// <param name="maxContentLength">Maximum character length for evidence content.</param>
    /// <returns>The resolved prompt string.</returns>
    public string GenerateAnalyzeEvidencePrompt(int maxContentLength = 12000)
    {
        var content = this.ReadAndCombineFiles(
            new[] { "metadata.md", "summary.md", "discussion.md", "rca.md" },
            maxContentLength);

        this.logger.LogInformation("Generating analyze-evidence prompt ({Length} chars)", content.Length);

        return this.BuildPrompt("analyze-evidence", new Dictionary<string, string>
        {
            ["evidence_content"] = content
        });
    }

    /// <summary>
    /// Generates the find-tsg prompt.
    /// </summary>
    /// <returns>The resolved prompt string.</returns>
    public string GenerateFindTsgPrompt()
    {
        var metadata = this.ParseMetadata();
        var incidentId = Path.GetFileName(this.incidentFolder);
        var contextFiles = new[] { "metadata.md", "summary.md", "issues_list.md", "code_issues.md", "etw_events.md" };
        var available = contextFiles
            .Where(f => File.Exists(Path.Combine(this.incidentFolder, f)))
            .ToList();

        var telemetryDir = Path.Combine(this.incidentFolder, "kusto", "telemetry");
        var hasTelemetry = Directory.Exists(telemetryDir)
            && Directory.GetFiles(telemetryDir, "*.csv").Length > 0;
        var telemetryNote = hasTelemetry
            ? $"**Telemetry data:** Available in {telemetryDir}"
            : string.Empty;

        // Resolve TSG repo path: "hub-tsg" for AzureStack/AzureStackIcm, "tsg" otherwise
        var tsgRepoKey = metadata.IsHubService() ? "hub-tsg" : "tsg";
        this.repoMappings.TryGetValue(tsgRepoKey, out var tsgRepoPath);

        this.logger.LogInformation("Generating find-tsg prompt for incident {Id}, TSG repo: {TsgRepo}", incidentId, tsgRepoPath);

        return this.BuildPrompt("find-tsg", new Dictionary<string, string>
        {
            ["incident_id"] = incidentId,
            ["incident_folder"] = this.incidentFolder,
            ["tsg_repo_path"] = tsgRepoPath ?? "",
            ["context_files"] = string.Join(", ", available),
            ["telemetry_note"] = telemetryNote,
            ["output_file"] = Path.Combine(this.incidentFolder, "applicable_tsgs.md")
        });
    }

    /// <summary>
    /// Generates the rca-generator prompt.
    /// </summary>
    /// <param name="outputFile">Path to write the output file.</param>
    /// <returns>The resolved prompt string.</returns>
    public string GenerateRcaGeneratorPrompt()
    {
        var incidentId = Path.GetFileName(this.incidentFolder);
        var contextFiles = new HashSet<string>
        {
            "metadata.md",
            "summary.md",
            "issues_list.md",
            "code_issues.md",
            "etw_events.md",
            "similar_incidents.md",
            "applicable_tsgs.md"
        }.Select(f => Path.Combine(this.incidentFolder, f)).ToList();

        var telemetryDir = Path.Combine(this.incidentFolder, "kusto", "telemetry");
        if (Directory.Exists(telemetryDir))
        {
            var csvFiles = Directory.GetFiles(telemetryDir, "*.csv");
            if (csvFiles.Length > 0)
            {
                foreach (var csvFile in csvFiles)
                {
                    if (File.ReadAllLines(csvFile).Length > 0)
                    {
                        var queryAnalysisFile = Path.ChangeExtension(csvFile, "md");
                        if (File.Exists(queryAnalysisFile))
                        {
                            contextFiles.Add(queryAnalysisFile);
                        }
                    }
                }
            }
        }

        var kustoArtifacts = this.DiscoverKustoArtifacts();

        this.logger.LogInformation("Generating rca-generator prompt for incident {Id} with {FileCount} context files, {KustoCount} kusto artifacts",
            incidentId, contextFiles.Count, kustoArtifacts.Count);

        return this.BuildPrompt("rca-generator", new Dictionary<string, string>
        {
            ["incident_id"] = incidentId,
            ["incident_folder"] = this.incidentFolder,
            ["context_files"] = string.Join("\n", contextFiles.Select(f => $"- {f}")),
            ["kusto_artifacts"] = kustoArtifacts.Count > 0
                ? string.Join("\n", kustoArtifacts)
                : "No kusto query results available.",
            ["output_file"] = Path.Combine(this.incidentFolder, "mitigation_steps.md")
        });
    }

    /// <summary>
    /// Generates the analyze-query-results prompt.
    /// </summary>
    /// <returns>The resolved prompt string.</returns>
    public string GenerateAnalyzeQueryResultsPrompt()
    {
        this.logger.LogInformation("Generating analyze-query-results prompt");

        return this.BuildPrompt("analyze-query-results", new Dictionary<string, string>
        {
            ["incident_folder"] = this.incidentFolder
        });
    }

    /// <summary>
    /// Generates a prompt by name with the given parameters.
    /// Falls back to raw template loading if no typed method exists.
    /// </summary>
    /// <param name="promptName">The prompt template name.</param>
    /// <param name="parameters">Placeholder values.</param>
    /// <returns>The resolved prompt string.</returns>
    public string BuildPrompt(string promptName, Dictionary<string, string> parameters)
    {
        var template = this.LoadTemplate(promptName);
        if (template == null)
        {
            throw new FileNotFoundException($"Prompt template '{promptName}' not found in {this.promptsBasePath}");
        }

        return this.ResolveTemplate(template, parameters);
    }

    /// <summary>
    /// Parses metadata.md in the incident folder to extract incident metadata fields.
    /// The file uses a markdown table with Field and Value columns.
    /// </summary>
    /// <returns>Dictionary of field names to values.</returns>
    private IncidentMetadata ParseMetadata()
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(this.incidentFolder, "metadata.md");
        if (!File.Exists(file))
        {
            this.logger.LogWarning("metadata.md not found in {Folder}", this.incidentFolder);
            return default;
        }

        var content = File.ReadAllText(file);
        if (string.IsNullOrWhiteSpace(content))
        {
            return default;
        }

        var tableRowPattern = new Regex(@"^\|\s*([^|]+)\s*\|\s*([^|]*)\s*\|");
        foreach (var line in content.Split('\n'))
        {
            var match = tableRowPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var field = match.Groups[1].Value.Trim();
            var value = match.Groups[2].Value.Trim();

            // Skip header and separator rows
            if (field == "Field" || Regex.IsMatch(field, @"^-+$"))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(field))
            {
                fields[field] = value;
            }
        }

        this.logger.LogDebug("Parsed {Count} metadata fields from metadata.md", fields.Count);

        return new IncidentMetadata
        {
            IncidentId = fields.GetValueOrDefault("IncidentId", ""),
            Status = fields.GetValueOrDefault("Status", ""),
            Service = fields.GetValueOrDefault("Service", ""),
            Team = fields.GetValueOrDefault("Team", ""),
            Owner = fields.GetValueOrDefault("Owner", ""),
            Title = fields.GetValueOrDefault("Title", ""),
            AEODeviceARMResourceUri = fields.GetValueOrDefault("AEODeviceARMResourceUri", ""),
            AEORegion = fields.GetValueOrDefault("AEORegion", ""),
            ImpactStartTime = DateTimeOffset.TryParse(fields.GetValueOrDefault("ImpactStartTime"), out var impact) ? impact : null,
            TimeRangeEnd = DateTimeOffset.TryParse(fields.GetValueOrDefault("TimeRangeEnd"), out var end) ? end : null
        };
    }

    /// <summary>
    /// Parses the repo mapping file to build a dictionary of repo name to local path.
    /// The file contains markdown tables with columns: | name | path | uri | description | private |
    /// grouped under category headers (## Hub, ## Hci, ## ARC, ## MOC).
    /// </summary>
    /// <returns>Dictionary mapping repo name to local path.</returns>
    private Dictionary<string, string> ParseRepoMapping()
    {
        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(RepoMappingFile))
        {
            this.logger.LogWarning("Repo mapping file not found: {Path}", RepoMappingFile);
            return mapping;
        }

        var content = File.ReadAllText(RepoMappingFile);
        if (string.IsNullOrWhiteSpace(content))
        {
            return mapping;
        }

        var tableRowPattern = new Regex(@"^\|\s*([^|]+)\s*\|\s*([^|]+)\s*\|\s*([^|]+)\s*\|\s*([^|]+)\s*\|\s*([^|]+)\s*\|");
        foreach (var line in content.Split('\n'))
        {
            var match = tableRowPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups[1].Value.Trim();
            var path = match.Groups[2].Value.Trim();

            // Skip header and separator rows
            if (name == "name" || Regex.IsMatch(name, @"^-+$"))
            {
                continue;
            }

            // Only include entries with a valid local path
            if (!string.IsNullOrEmpty(name) && Regex.IsMatch(path, @"^[a-zA-Z]:\\"))
            {
                mapping[name] = path;
            }
        }

        this.logger.LogDebug("Parsed {Count} repo mappings from {Path}", mapping.Count, RepoMappingFile);
        return mapping;
    }

    /// <summary>
    /// Parses repo_identified.md in the incident folder to extract repository paths.
    /// Supports two formats:
    /// 1. Table rows: | Repository | Path | Team | Confidence | Evidence |
    /// 2. Free-form: ### N. **Name (`repo`)** followed by **Path:** `path`
    /// </summary>
    /// <returns>List of repository paths found in the file.</returns>
    private List<string> ParseRepoPaths()
    {
        var file = Path.Combine(this.incidentFolder, "repo_identified.md");
        if (!File.Exists(file))
        {
            this.logger.LogWarning("repo_identified.md not found in {Folder}", this.incidentFolder);
            return [];
        }

        var content = File.ReadAllText(file);
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var paths = new List<string>();
        var lines = content.Split('\n');

        // Strategy 1: Parse table rows — | Repository | Path | Team | Confidence | Evidence |
        var tableRowPattern = new Regex(@"^\|\s*([^|*]+)\s*\|\s*([^|]+)\s*\|\s*([^|]+)\s*\|\s*([^|]+)\s*\|\s*([^|]+)\s*\|");
        foreach (var line in lines)
        {
            var match = tableRowPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var repo = match.Groups[1].Value.Trim();
            var path = match.Groups[2].Value.Trim();

            // Skip header and separator rows
            if (repo == "Repository" || Regex.IsMatch(repo, @"^-+$"))
            {
                continue;
            }

            if (Regex.IsMatch(path, @"^[a-zA-Z]:\\"))
            {
                paths.Add(path);
            }
        }

        // Strategy 2: Fallback — parse free-form "### N. **Name (`repo`)**" + "**Path:** `path`"
        if (paths.Count == 0)
        {
            var pathLinePattern = new Regex(@"\*\*Path:\*\*\s*`?([a-zA-Z]:\\[^`\s]+)`?");
            foreach (var line in lines)
            {
                var match = pathLinePattern.Match(line);
                if (match.Success)
                {
                    paths.Add(match.Groups[1].Value.Trim());
                }
            }
        }

        this.logger.LogInformation("Parsed {Count} repo paths from repo_identified.md", paths.Count);
        return paths;
    }

    /// <summary>
    /// Reads and combines content from multiple files in the incident folder.
    /// Truncates if total length exceeds the limit.
    /// </summary>
    private string ReadAndCombineFiles(string[] fileNames, int maxLength)
    {
        var parts = new List<string>();
        foreach (var fileName in fileNames)
        {
            var filePath = Path.Combine(this.incidentFolder, fileName);
            if (File.Exists(filePath))
            {
                var content = File.ReadAllText(filePath);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    parts.Add(content);
                }
            }
        }

        var combined = string.Join("\n\n", parts);
        if (combined.Length > maxLength)
        {
            combined = combined[..maxLength] + "\n... [truncated]";
            this.logger.LogDebug("Content truncated from {Original} to {Max} chars", combined.Length, maxLength);
        }

        return combined;
    }

    /// <summary>
    /// Discovers kusto query/result pairs in kusto/ subfolders.
    /// Returns formatted lines like: "- kusto/telemetry/query.md + query.csv (42 rows)"
    /// </summary>
    private List<string> DiscoverKustoArtifacts()
    {
        var artifacts = new List<string>();
        var kustoDir = Path.Combine(this.incidentFolder, "kusto");
        if (!Directory.Exists(kustoDir))
        {
            return artifacts;
        }

        foreach (var subdir in new[] { "telemetry", "diagnostics", "hub" })
        {
            var subdirPath = Path.Combine(kustoDir, subdir);
            if (!Directory.Exists(subdirPath))
            {
                continue;
            }

            var mdFiles = Directory.GetFiles(subdirPath, "*.md");
            var csvFiles = Directory.GetFiles(subdirPath, "*.csv")
                .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => f, StringComparer.OrdinalIgnoreCase);

            foreach (var mdFile in mdFiles)
            {
                var baseName = Path.GetFileNameWithoutExtension(mdFile);
                if (csvFiles.TryGetValue(baseName, out var csvFile))
                {
                    var rowCount = File.ReadLines(csvFile).Count() - 1;
                    artifacts.Add($"- kusto/{subdir}/{Path.GetFileName(mdFile)} + {Path.GetFileName(csvFile)} ({rowCount} rows)");
                }
                else
                {
                    artifacts.Add($"- kusto/{subdir}/{Path.GetFileName(mdFile)} (no results)");
                }
            }
        }

        return artifacts;
    }
}