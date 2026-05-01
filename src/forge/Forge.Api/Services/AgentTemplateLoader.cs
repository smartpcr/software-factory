// -----------------------------------------------------------------------
// <copyright file="AgentTemplateLoader.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

using System.Collections.Concurrent;

/// <summary>
/// Phase 24 Q24.13 — single resolver for the per-task system-prompt
/// templates under <c>agents/templates/*.md</c>. The runtime concatenates
/// <c>&lt;preamble&gt;</c> + the matching template + <c>&lt;soul.md body&gt;</c>
/// per Phase 14.6's preamble injector contract. Files are read once and
/// cached; the cache is invalidated on every read by checking the file's
/// last-write timestamp so operators can edit a deployed template without
/// restarting the API.
/// </summary>
public static class AgentTemplateLoader
{
    /// <summary>The comment-response template — Q24.13 must-reply rubric.</summary>
    public const string CommentResponseFile = "comment-response-system.md";

    /// <summary>Coding template. Used by <see cref="LoadAsync"/> when callers
    /// extend Phase 13.5's coding pipeline through the same template loader.</summary>
    public const string CodingFile = "coding-system.md";

    /// <summary>Plan template — Phase 13.4.</summary>
    public const string PlanFile = "plan-system.md";

    /// <summary>Build-fix template — forge_implementation.md Phase 27 Q27.10.</summary>
    public const string BuildFixFile = "build-fix-system.md";

    private static readonly ConcurrentDictionary<string, (DateTime LastWrite, string Content)> Cache = new();

    /// <summary>
    /// Returns the template content as a string, or empty when the file is
    /// missing (best-effort — a missing template falls back to the soul.md
    /// alone, which is the pre-Phase-24 behaviour).
    /// </summary>
    public static async Task<string> LoadAsync(string fileName, CancellationToken ct = default)
    {
        var path = ResolvePath(fileName);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return string.Empty;

        var lastWrite = File.GetLastWriteTimeUtc(path);
        if (Cache.TryGetValue(path, out var cached) && cached.LastWrite == lastWrite)
        {
            return cached.Content;
        }

        var content = await File.ReadAllTextAsync(path, ct);
        Cache[path] = (lastWrite, content);
        return content;
    }

    /// <summary>
    /// Resolve the absolute path to a template file. Searches:
    /// <list type="number">
    /// <item><c>$Forge:Agents:TemplatesDir</c> when set.</item>
    /// <item><c>{AppContext.BaseDirectory}/agents/templates/</c> — the
    /// build-output copy via csproj <c>Content Include</c>.</item>
    /// <item>The repo root <c>agents/templates/</c> — useful when running
    /// from the workspace without a publish step.</item>
    /// </list>
    /// Returns null when the file isn't found in any of the locations.
    /// </summary>
    public static string? ResolvePath(string fileName)
    {
        var configured = Environment.GetEnvironmentVariable("FORGE_AGENT_TEMPLATES_DIR");
        if (!string.IsNullOrEmpty(configured))
        {
            var p = Path.Combine(configured, fileName);
            if (File.Exists(p)) return p;
        }

        var fromBaseDir = Path.Combine(AppContext.BaseDirectory, "agents", "templates", fileName);
        if (File.Exists(fromBaseDir)) return fromBaseDir;

        // Walk up looking for a "agents/templates" folder — covers tests and
        // dev runs from anywhere under the repo.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            var candidate = Path.Combine(dir, "agents", "templates", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return null;
    }

    /// <summary>Test-hook — flush the cache so a unit test can swap a template mid-run.</summary>
    internal static void ClearCache() => Cache.Clear();
}
