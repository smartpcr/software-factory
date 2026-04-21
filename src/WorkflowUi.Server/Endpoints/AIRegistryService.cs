// -----------------------------------------------------------------------
// <copyright file="AIRegistryService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Endpoints;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Reads AI agent and skill definitions from disk (YAML-frontmatter markdown files).
/// Results are cached and refreshed when the underlying directories change.
/// </summary>
public sealed class AIRegistryService : IDisposable
{
    private readonly ILogger<AIRegistryService> logger;
    private readonly AIRegistryOptions options;
    private List<AIRegistryItem> agents = [];
    private List<AIRegistryItem> skills = [];
    private FileSystemWatcher? agentWatcher;
    private FileSystemWatcher? skillWatcher;
    private volatile bool dirty = true;

    public AIRegistryService(IOptions<AIRegistryOptions> options, ILogger<AIRegistryService> logger)
    {
        this.logger = logger;
        this.options = options.Value;
        this.InitWatchers();
    }

    public IReadOnlyList<AIRegistryItem> GetAgents()
    {
        this.EnsureLoaded();
        return this.agents;
    }

    public IReadOnlyList<AIRegistryItem> GetSkills()
    {
        this.EnsureLoaded();
        return this.skills;
    }

    public void Dispose()
    {
        this.agentWatcher?.Dispose();
        this.skillWatcher?.Dispose();
    }

    private void EnsureLoaded()
    {
        if (!this.dirty)
        {
            return;
        }

        this.agents = this.LoadAgents();
        this.skills = this.LoadSkills();
        this.dirty = false;
    }

    private List<AIRegistryItem> LoadAgents()
    {
        var dir = this.options.AgentsPath;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            this.logger.LogWarning("Agents directory not found: {Path}", dir);
            return [];
        }

        var items = new List<AIRegistryItem>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
        {
            var frontmatter = ParseYamlFrontmatter(file);
            if (frontmatter.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name))
            {
                frontmatter.TryGetValue("description", out var desc);
                frontmatter.TryGetValue("model", out var model);
                items.Add(new AIRegistryItem(name, desc ?? "", model));
            }
        }

        this.logger.LogInformation("Loaded {Count} agents from {Path}", items.Count, dir);
        return items.OrderBy(a => a.Name).ToList();
    }

    private List<AIRegistryItem> LoadSkills()
    {
        var dir = this.options.SkillsPath;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            this.logger.LogWarning("Skills directory not found: {Path}", dir);
            return [];
        }

        var items = new List<AIRegistryItem>();
        foreach (var subDir in Directory.EnumerateDirectories(dir))
        {
            var skillMd = Path.Combine(subDir, "SKILL.md");
            if (!File.Exists(skillMd))
            {
                continue;
            }

            var frontmatter = ParseYamlFrontmatter(skillMd);
            if (frontmatter.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name))
            {
                frontmatter.TryGetValue("description", out var desc);
                items.Add(new AIRegistryItem(name, desc ?? "", null));
            }
        }

        this.logger.LogInformation("Loaded {Count} skills from {Path}", items.Count, dir);
        return items.OrderBy(s => s.Name).ToList();
    }

    private static Dictionary<string, string> ParseYamlFrontmatter(string filePath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var reader = new StreamReader(filePath);
            var firstLine = reader.ReadLine();
            if (firstLine?.TrimStart() != "---")
            {
                return result;
            }

            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (line == null || line.TrimStart() == "---")
                {
                    break;
                }

                var colonIdx = line.IndexOf(':');
                if (colonIdx > 0)
                {
                    var key = line[..colonIdx].Trim();
                    var value = line[(colonIdx + 1)..].Trim();
                    result[key] = value;
                }
            }
        }
        catch
        {
            // Silently skip files that can't be read
        }

        return result;
    }

    private void InitWatchers()
    {
        if (!string.IsNullOrEmpty(this.options.AgentsPath) && Directory.Exists(this.options.AgentsPath))
        {
            this.agentWatcher = new FileSystemWatcher(this.options.AgentsPath, "*.md")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            this.agentWatcher.Changed += (_, _) => this.dirty = true;
            this.agentWatcher.Created += (_, _) => this.dirty = true;
            this.agentWatcher.Deleted += (_, _) => this.dirty = true;
        }

        if (!string.IsNullOrEmpty(this.options.SkillsPath) && Directory.Exists(this.options.SkillsPath))
        {
            this.skillWatcher = new FileSystemWatcher(this.options.SkillsPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };
            this.skillWatcher.Changed += (_, _) => this.dirty = true;
            this.skillWatcher.Created += (_, _) => this.dirty = true;
            this.skillWatcher.Deleted += (_, _) => this.dirty = true;
        }
    }
}

/// <summary>
/// Configuration for AI registry paths.
/// </summary>
public class AIRegistryOptions
{
    public const string SectionName = "WorkflowUi:AIRegistry";

    /// <summary>Path to directory containing agent .md files.</summary>
    public string AgentsPath { get; set; } = string.Empty;

    /// <summary>Path to directory containing skill subdirectories.</summary>
    public string SkillsPath { get; set; } = string.Empty;
}

/// <summary>
/// Describes an available AI agent or skill.
/// </summary>
public record AIRegistryItem(string Name, string Description, string? Model);
