// -----------------------------------------------------------------------
// <copyright file="FileAIPromptResolver.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.AI;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;

/// <summary>
/// Resolves agent/skill definitions by reading .md files and using an LLM (Sonnet 4.6 via
/// Copilot CLI) to extract structured metadata. Results are cached as .inputs.json sidecar
/// files and only re-extracted when the source .md file changes.
/// </summary>
public class FileAIPromptResolver : IAIPromptResolver
{
    private const string ExtractionModel = "claude-sonnet-4.6";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string agentsBasePath;
    private readonly string skillsBasePath;
    private readonly ILogger<FileAIPromptResolver> logger;

    public FileAIPromptResolver(string agentsBasePath, string skillsBasePath, ILogger<FileAIPromptResolver> logger)
    {
        this.agentsBasePath = agentsBasePath;
        this.skillsBasePath = skillsBasePath;
        this.logger = logger;
    }

    public async Task<AgentDefinitionInfo?> LoadAgentAsync(string agentName, CancellationToken ct = default)
    {
        var mdPath = Path.Combine(this.agentsBasePath, $"{agentName}.md");
        if (!File.Exists(mdPath))
        {
            this.logger.LogWarning("Agent definition not found: {Path}", mdPath);
            return null;
        }

        return await this.LoadAndExtractAsync(mdPath, agentName, ct);
    }

    public async Task<AgentDefinitionInfo?> LoadSkillAsync(string skillName, CancellationToken ct = default)
    {
        var mdPath = Path.Combine(this.skillsBasePath, skillName, "SKILL.md");
        if (!File.Exists(mdPath))
        {
            this.logger.LogWarning("Skill definition not found: {Path}", mdPath);
            return null;
        }

        return await this.LoadAndExtractAsync(mdPath, skillName, ct);
    }

    public async Task<string> GenerateUserPromptAsync(
        AgentDefinitionInfo agentInfo,
        Dictionary<string, string> resolvedBindings,
        string? additionalContext,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Given this agent/skill summary and the provided parameter values, generate a clear, specific user prompt.");
        sb.AppendLine();
        sb.AppendLine($"## Agent/Skill: {agentInfo.Name}");
        sb.AppendLine($"**Summary**: {agentInfo.Summary}");
        sb.AppendLine();

        if (resolvedBindings.Count > 0)
        {
            sb.AppendLine("## Parameter Values");
            foreach (var kvp in resolvedBindings)
            {
                sb.AppendLine($"- **{kvp.Key}**: {kvp.Value}");
            }

            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(additionalContext))
        {
            sb.AppendLine("## Additional Context");
            sb.AppendLine(additionalContext);
            sb.AppendLine();
        }

        sb.AppendLine("Generate only the user prompt — no explanations. The prompt should tell the agent exactly what to do with the given parameters.");

        var result = await RunCopilotCliAsync(sb.ToString(), ExtractionModel, ct);
        return !string.IsNullOrWhiteSpace(result) ? result.Trim() : BuildFallbackPrompt(agentInfo, resolvedBindings, additionalContext);
    }

    private async Task<AgentDefinitionInfo> LoadAndExtractAsync(string mdPath, string name, CancellationToken ct)
    {
        var mdContent = await File.ReadAllTextAsync(mdPath, ct);
        var currentHash = ComputeHash(mdContent);

        // Check for cached sidecar
        var cachePath = Path.ChangeExtension(mdPath, ".inputs.json");
        if (File.Exists(cachePath))
        {
            try
            {
                var cacheJson = await File.ReadAllTextAsync(cachePath, ct);
                var cached = JsonSerializer.Deserialize<AgentDefinitionInfo>(cacheJson, JsonOpts);
                if (cached != null && cached.SourceHash == currentHash)
                {
                    this.logger.LogDebug("Using cached extraction for {Name}", name);
                    cached.SystemPrompt = ExtractBodyAfterFrontmatter(mdContent);
                    cached.ScriptPath ??= DetectMainScript(mdPath);
                    return cached;
                }
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Failed to read cache for {Name}, will re-extract", name);
            }
        }

        // Extract via LLM
        this.logger.LogInformation("Extracting metadata for {Name} via {Model}...", name, ExtractionModel);
        var info = await this.ExtractWithLlmAsync(mdContent, name, ct);
        info.SourceHash = currentHash;
        info.ExtractedAt = DateTime.UtcNow;
        info.SystemPrompt = ExtractBodyAfterFrontmatter(mdContent);
        info.ScriptPath = DetectMainScript(mdPath);

        // Write cache sidecar
        try
        {
            var cacheClone = new AgentDefinitionInfo
            {
                Name = info.Name,
                Description = info.Description,
                Summary = info.Summary,
                ModelHint = info.ModelHint,
                AllowedTools = info.AllowedTools,
                Inputs = info.Inputs,
                SourceHash = info.SourceHash,
                ExtractedAt = info.ExtractedAt,
                // Don't store SystemPrompt in cache — it can be huge
            };
            var json = JsonSerializer.Serialize(cacheClone, JsonOpts);
            await File.WriteAllTextAsync(cachePath, json, ct);
            this.logger.LogInformation("Cached extraction for {Name} → {Path}", name, cachePath);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Failed to write cache for {Name}", name);
        }

        return info;
    }

    private async Task<AgentDefinitionInfo> ExtractWithLlmAsync(string mdContent, string name, CancellationToken ct)
    {
        // Truncate very large files for extraction
        var inputText = mdContent.Length > 15000 ? mdContent[..15000] + "\n\n[... truncated ...]" : mdContent;

        var jsonSchema = """
            {
              "name": "agent-name",
              "description": "one-line description",
              "summary": "2-3 sentence summary of what this agent/skill does and how it works",
              "modelHint": "opus|sonnet|haiku or null",
              "inputs": [
                { "name": "paramName", "type": "string|integer|boolean|object", "required": true, "description": "what this param is" }
              ]
            }
            """;

        var prompt = "Analyze the following agent/skill definition file and extract structured metadata.\n"
            + "Return ONLY valid JSON with no markdown fencing, in this exact schema:\n"
            + jsonSchema + "\n\n"
            + "Rules for extracting inputs:\n"
            + "- Look for CLI parameters, function parameters, required arguments, environment variables, and documented inputs\n"
            + "- Include params from Usage/CLI sections, function signatures, and input descriptions\n"
            + "- For PowerShell scripts, extract from param() blocks\n"
            + "- For markdown-only agents, extract from \"Input\" or \"Task\" sections\n"
            + "- Mark required vs optional based on documentation\n\n"
            + "--- FILE CONTENT ---\n"
            + inputText;

        var rawJson = await RunCopilotCliAsync(prompt, ExtractionModel, ct);
        return ParseExtractionResult(rawJson, name);
    }

    private static AgentDefinitionInfo ParseExtractionResult(string rawJson, string fallbackName)
    {
        var info = new AgentDefinitionInfo { Name = fallbackName };

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return info;
        }

        try
        {
            // Strip markdown fencing if present
            var json = rawJson.Trim();
            if (json.StartsWith("```"))
            {
                var firstNewline = json.IndexOf('\n');
                json = json[(firstNewline + 1)..];
                var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0)
                {
                    json = json[..lastFence];
                }
            }

            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("name", out var n)) info.Name = n.GetString() ?? fallbackName;
            if (root.TryGetProperty("description", out var d)) info.Description = d.GetString() ?? "";
            if (root.TryGetProperty("summary", out var s)) info.Summary = s.GetString() ?? "";
            if (root.TryGetProperty("modelHint", out var m) && m.ValueKind == JsonValueKind.String) info.ModelHint = m.GetString();

            if (root.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
            {
                foreach (var inp in inputs.EnumerateArray())
                {
                    info.Inputs.Add(new AgentInputParam
                    {
                        Name = inp.TryGetProperty("name", out var pn) ? pn.GetString() ?? "" : "",
                        Type = inp.TryGetProperty("type", out var pt) ? pt.GetString() ?? "string" : "string",
                        Required = inp.TryGetProperty("required", out var pr) && pr.ValueKind == JsonValueKind.True,
                        Description = inp.TryGetProperty("description", out var pd) ? pd.GetString() ?? "" : "",
                    });
                }
            }
        }
        catch
        {
            // If parsing fails, return with defaults
        }

        return info;
    }

    private static async Task<string> RunCopilotCliAsync(string prompt, string model, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(120));

            await using var client = new CopilotClient();
            var session = await client.CreateSessionAsync(new SessionConfig
            {
                Model = model,
                OnPermissionRequest = async (req, invocation) =>
                    new PermissionRequestResult { Kind = PermissionRequestResultKind.Approved },
            }, cts.Token);

            try
            {
                var response = await session.SendAndWaitAsync(
                    new MessageOptions { Prompt = prompt },
                    TimeSpan.FromSeconds(90),
                    cts.Token);
                return response?.Data?.Content?.Trim() ?? string.Empty;
            }
            finally
            {
                try { await session.DisposeAsync(); } catch { /* best-effort */ }
            }
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RunCopilotCliAsync failed: {ex.Message}");
            return string.Empty;
        }
    }

    private static string ExtractBodyAfterFrontmatter(string content)
    {
        var lines = content.Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return content.Trim();
        }

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd('\r').Trim() == "---")
            {
                return string.Join('\n', lines[(i + 1)..]).Trim();
            }
        }

        return content.Trim();
    }

    private static string BuildFallbackPrompt(
        AgentDefinitionInfo agentInfo,
        Dictionary<string, string> bindings,
        string? additionalContext)
    {
        var sb = new StringBuilder();
        if (bindings.Count > 0)
        {
            foreach (var kvp in bindings)
            {
                sb.AppendLine($"- {kvp.Key}: {kvp.Value}");
            }
        }

        if (!string.IsNullOrEmpty(additionalContext))
        {
            sb.AppendLine(additionalContext);
        }

        return sb.ToString().Trim();
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Detects the main executable script in the same directory as the .md file.
    /// Looks for a .ps1 file matching the skill/agent name, then falls back to any .ps1.
    /// </summary>
    private static string? DetectMainScript(string mdPath)
    {
        var dir = Path.GetDirectoryName(mdPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(mdPath);
        // For SKILL.md, use the directory name
        if (name.Equals("SKILL", StringComparison.OrdinalIgnoreCase))
        {
            name = new DirectoryInfo(dir).Name;
        }

        // Try exact name match first
        var exactMatch = Path.Combine(dir, $"{name}.ps1");
        if (File.Exists(exactMatch))
        {
            return exactMatch;
        }

        // Fall back to any .ps1 in the directory (skip test files)
        var scripts = Directory.GetFiles(dir, "*.ps1")
            .Where(f => !Path.GetFileName(f).Contains("test", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return scripts.Length == 1 ? scripts[0] : null;
    }
}