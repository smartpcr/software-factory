// -----------------------------------------------------------------------
// <copyright file="SoulService.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Agents;

using Microsoft.Extensions.Options;

/// <summary>
/// Phase 11.3 — default <see cref="ISoulService"/>. Reads / writes the file
/// directly; a future Phase R upgrade can layer a file-watcher invalidation
/// hook on top. YAML front-matter parsing is intentionally minimal — only
/// the simple <c>tools</c> list is recognised, because that's all the runner
/// needs to populate <see cref="AgentContext.AllowedTools"/>.
/// </summary>
public sealed class SoulService : ISoulService
{
    private readonly IOptions<SoulOptions> options;

    public SoulService(IOptions<SoulOptions> options)
    {
        this.options = options;
    }

    public string ResolvePath(string agentId, string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(overridePath);
        var root = this.options.Value.Root ?? DefaultRoot();
        return Path.Combine(root, agentId, "soul.md");
    }

    public async Task<SoulDocument?> ReadAsync(string agentId, string? overridePath, CancellationToken ct)
    {
        var path = this.ResolvePath(agentId, overridePath);
        if (!File.Exists(path)) return null;
        var raw = await File.ReadAllTextAsync(path, ct);
        var (body, caps) = ParseFrontMatter(raw);
        return new SoulDocument(path, raw, body, caps);
    }

    public async Task WriteAsync(string agentId, string? overridePath, string contents, CancellationToken ct)
    {
        var path = this.ResolvePath(agentId, overridePath);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, contents, ct);
    }

    private static string DefaultRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "Forge", "agents");
    }

    /// <summary>
    /// Tiny YAML-front-matter parser: looks for a leading <c>---</c> block,
    /// pulls a <c>tools:</c> list (either flow <c>[a, b]</c> or block-with-dashes),
    /// and returns the body that follows.
    /// </summary>
    public static (string Body, IReadOnlyList<string> Capabilities) ParseFrontMatter(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return (string.Empty, Array.Empty<string>());
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return (raw, Array.Empty<string>());

        var endIdx = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---") { endIdx = i; break; }
        }
        if (endIdx < 0) return (raw, Array.Empty<string>());

        var caps = new List<string>();
        for (var i = 1; i < endIdx; i++)
        {
            var line = lines[i];
            var trim = line.TrimStart();
            if (!trim.StartsWith("tools:", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = trim.Substring("tools:".Length).Trim();
            if (rest.StartsWith('[') && rest.EndsWith(']'))
            {
                // Flow form: tools: [read_file, write_file]
                foreach (var item in rest.Trim('[', ']').Split(','))
                {
                    var v = item.Trim().Trim('"', '\'');
                    if (!string.IsNullOrEmpty(v)) caps.Add(v);
                }
                break;
            }
            if (string.IsNullOrEmpty(rest))
            {
                // Block form:
                //   tools:
                //     - read_file
                //     - write_file
                for (var j = i + 1; j < endIdx; j++)
                {
                    var sub = lines[j].TrimStart();
                    if (!sub.StartsWith('-')) break;
                    var v = sub.TrimStart('-').Trim().Trim('"', '\'');
                    if (!string.IsNullOrEmpty(v)) caps.Add(v);
                }
                break;
            }
        }

        var body = string.Join("\n", lines.Skip(endIdx + 1)).TrimStart();
        return (body, caps);
    }

    /// <summary>
    /// True when the soul front-matter declares a <c>model:</c> key. Such a key
    /// is FORBIDDEN: the runtime model is always resolved from the crew/agent DB
    /// settings (<c>CrewStoryArtifactStrategy.ResolveAgentRunConfigAsync</c> →
    /// <c>agent.EvaluatorModel/GeneratorModel ?? crew.EvaluatorModel/Model</c>),
    /// never from the soul file, which is injected verbatim only as persona
    /// text. A stale soul <c>model:</c> (e.g. a Sonnet header on a gpt-5.5
    /// evaluator) is silently ignored and only invites misdiagnosis, so souls
    /// must not carry it. Enforced by <c>SoulModelFrontmatterTests</c>.
    /// </summary>
    public static bool DeclaresModel(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return false;
        for (var i = 1; i < lines.Length; i++)
        {
            var trim = lines[i].Trim();
            if (trim == "---") return false;
            if (trim.StartsWith("model:", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

/// <summary>Bound from <c>Forge:Agents:SoulRoot</c>.</summary>
public sealed class SoulOptions
{
    public const string SectionName = "Forge:Agents:Soul";

    /// <summary>Override the default <c>%LOCALAPPDATA%\Forge\agents</c> root (useful for tests).</summary>
    public string? Root { get; set; }
}
