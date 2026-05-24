// -----------------------------------------------------------------------
// <copyright file="WorkspaceEcosystemDetector.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

/// <summary>
/// docs/forge/e2e.md Stage 4.3 — the single source of truth for "what
/// language/ecosystem is this workspace?". Both the qa-e2e generator
/// (<c>CrewStoryArtifactStrategy.ResolveRepoEcosystem</c> — which test
/// harness/lib to scaffold) and the impacted-test selection chain
/// (<c>LspImpactedTestProvider</c> — which language server to launch)
/// route their detection through here so the two subsystems can never
/// disagree about a repo's language.
///
/// <para>Detection is two-tier, both purely structural (no processes,
/// no network):</para>
/// <list type="number">
///   <item><b>Root fast-path</b> — <see cref="BuildSystemDetector.Detect"/>
///         scans the repo root for the canonical single-marker layout
///         (<c>*.sln</c>, <c>package.json</c>, <c>Cargo.toml</c>,
///         <c>go.mod</c>, <c>pom.xml</c>, <c>pyproject.toml</c>). When it
///         finds one, that's authoritative.</item>
///   <item><b>Monorepo deep scan</b> — when the root is unmarked (many
///         repos keep their module marker in a sub-tree, e.g.
///         smartpcr/code-intelligence whose only <c>go.mod</c> lives at
///         <c>services/agent-memory/go.mod</c>), walk a bounded depth
///         counting marker files AND source-file extensions, then score
///         each ecosystem as <c>markers*1000 + sourceFiles</c> so a stray
///         <c>requirements.txt</c> in a Go repo can't outweigh 181 <c>.go</c>
///         files vs 2 <c>.py</c>.</item>
/// </list>
///
/// <para>Returns a lowercase ecosystem token — <c>go</c> | <c>dotnet</c> |
/// <c>rust</c> | <c>node</c> | <c>python</c> | <c>maven</c> — or
/// <see cref="Unknown"/> when the tree yields no signal at all.</para>
/// </summary>
public static class WorkspaceEcosystemDetector
{
    /// <summary>Sentinel returned when no language marker / source file is found.</summary>
    public const string Unknown = "unknown";

    private const int MaxDepth = 4;
    private const int MaxFiles = 8000;

    /// <summary>
    /// Detect the predominant ecosystem at <paramref name="root"/>.
    /// Root fast-path first, then a bounded monorepo deep scan. Returns
    /// <see cref="Unknown"/> when nothing matches or on IO failure.
    /// </summary>
    public static string Detect(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Unknown;
        }

        // (1) root-level project markers (fast path, shared with repo-attach).
        var eco = BuildSystemDetector.Detect(root).Ecosystem;
        if (!string.IsNullOrEmpty(eco) && eco != Unknown)
        {
            return eco;
        }

        // (2) bounded monorepo deep scan.
        return DetectDeep(root) ?? Unknown;
    }

    /// <summary>
    /// Map an ecosystem token to the language id the impacted-test LSP
    /// chain keys its <c>ILspLanguageSupport</c> set on. Returns
    /// <c>null</c> for ecosystems with no stdio language server in the
    /// chain (<c>dotnet</c> goes to Roslyn in-process; <c>maven</c> /
    /// <see cref="Unknown"/> fall through to the heuristic).
    /// </summary>
    public static string? ToLspLanguageId(string ecosystem) => ecosystem switch
    {
        "go" => "go",
        "rust" => "rust",
        "python" => "python",
        "node" => "typescript",
        _ => null,
    };

    /// <summary>
    /// Bounded recursive ecosystem sniff for repos whose build marker
    /// isn't at the root. Walks at most <see cref="MaxDepth"/> directories
    /// (skipping VCS / vendor / build-output dirs) and counts ecosystem
    /// marker files; if none are found, falls back to the most common
    /// source-file extension ("everything is written in Go" ⇒ <c>go</c>).
    /// Returns null when the tree yields no signal.
    /// </summary>
    public static string? DetectDeep(string root)
    {
        try
        {
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git", ".worktree", ".forge", "node_modules", "vendor",
                "bin", "obj", "target", ".vs", ".idea", "dist", ".venv", "venv",
            };
            var markerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var srcExtCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((root, 0));
            var filesSeen = 0;

            while (queue.Count > 0 && filesSeen < MaxFiles)
            {
                var (dir, depth) = queue.Dequeue();
                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(dir); }
                catch { continue; }

                foreach (var entry in entries)
                {
                    if (Directory.Exists(entry))
                    {
                        if (depth + 1 <= MaxDepth && !skip.Contains(Path.GetFileName(entry)))
                        {
                            queue.Enqueue((entry, depth + 1));
                        }
                        continue;
                    }

                    if (++filesSeen > MaxFiles) break;
                    switch (Path.GetFileName(entry).ToLowerInvariant())
                    {
                        case "go.mod": Bump(markerCounts, "go"); break;
                        case "cargo.toml": Bump(markerCounts, "rust"); break;
                        case "pom.xml":
                        case "build.gradle":
                        case "build.gradle.kts": Bump(markerCounts, "maven"); break;
                        case "pyproject.toml":
                        case "requirements.txt": Bump(markerCounts, "python"); break;
                        case "package.json": Bump(markerCounts, "node"); break;
                    }
                    switch (Path.GetExtension(entry).ToLowerInvariant())
                    {
                        case ".sln":
                        case ".csproj": Bump(markerCounts, "dotnet"); break;
                        case ".go": Bump(srcExtCounts, "go"); break;
                        case ".cs": Bump(srcExtCounts, "dotnet"); break;
                        case ".rs": Bump(srcExtCounts, "rust"); break;
                        case ".py": Bump(srcExtCounts, "python"); break;
                        case ".ts":
                        case ".tsx":
                        case ".js": Bump(srcExtCounts, "node"); break;
                    }
                }
            }

            // Combine the two signals into one score per ecosystem. A marker
            // file (go.mod, *.csproj, …) is strong, but source-file
            // predominance must break ties — a stray `requirements.txt` in a Go
            // repo (go.mod==1, requirements.txt==1) must NOT outweigh 181 `.go`
            // files vs 2 `.py`. Score = markers*1000 + sourceFiles.
            var ecosystems = new HashSet<string>(markerCounts.Keys, StringComparer.Ordinal);
            ecosystems.UnionWith(srcExtCounts.Keys);
            string? best = null;
            var bestScore = -1;
            foreach (var eco in ecosystems)
            {
                var score = (markerCounts.TryGetValue(eco, out var m) ? m : 0) * 1000
                          + (srcExtCounts.TryGetValue(eco, out var s) ? s : 0);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = eco;
                }
            }
            return best;
        }
        catch
        {
            // best-effort sniff — any IO failure falls through to the caller's default.
            return null;
        }

        static void Bump(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out var v) ? v + 1 : 1;
    }
}
