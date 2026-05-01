// -----------------------------------------------------------------------
// <copyright file="BuildSystemDetector.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services;

/// <summary>
/// Phase 26 — filesystem-only scanner that detects build and test commands
/// from common project markers (<c>*.sln</c>, <c>package.json</c>, etc.).
/// No processes are started; detection is purely structural.
/// </summary>
public static class BuildSystemDetector
{
    /// <summary>Result of a build-system scan.</summary>
    public sealed record DetectedCommands(
        string? InitCommand,
        string? RestoreCommand,
        string? BuildCommand,
        string? TestCommand,
        string Ecosystem,
        bool Ambiguous,
        string? AmbiguityDetail);

    /// <summary>
    /// Scans <paramref name="repoRoot"/> for known project markers and
    /// returns the best-guess commands. Priority order:
    /// <list type="number">
    ///   <item>.sln (dotnet)</item>
    ///   <item>.csproj (dotnet, single project)</item>
    ///   <item>package.json (npm/node)</item>
    ///   <item>Cargo.toml (rust)</item>
    ///   <item>go.mod (go)</item>
    ///   <item>pom.xml (maven/java)</item>
    ///   <item>pyproject.toml / requirements.txt (python)</item>
    /// </list>
    /// Returns <c>Ecosystem = "unknown"</c> when no marker is found.
    /// </summary>
    public static DetectedCommands Detect(string repoRoot)
    {
        if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
            return Unknown();

        // ── 1. .NET: prefer *.sln over bare *.csproj ─────────────────
        var slnFiles = Directory.GetFiles(repoRoot, "*.sln", SearchOption.TopDirectoryOnly);
        if (slnFiles.Length == 1)
        {
            var sln = QuotePath(Path.GetFileName(slnFiles[0]));
            return new DetectedCommands(
                InitCommand: null,
                RestoreCommand: $"dotnet restore {sln}",
                BuildCommand: $"dotnet build {sln}",
                TestCommand: $"dotnet test {sln}",
                Ecosystem: "dotnet",
                Ambiguous: false,
                AmbiguityDetail: null);
        }
        if (slnFiles.Length > 1)
        {
            // Multiple .sln files — ambiguous. Return the first but flag it.
            var sln = QuotePath(Path.GetFileName(slnFiles[0]));
            return new DetectedCommands(
                InitCommand: null,
                RestoreCommand: $"dotnet restore {sln}",
                BuildCommand: $"dotnet build {sln}",
                TestCommand: $"dotnet test {sln}",
                Ecosystem: "dotnet",
                Ambiguous: true,
                AmbiguityDetail: $"Multiple .sln files found: {string.Join(", ", slnFiles.Select(Path.GetFileName))}. Using {Path.GetFileName(slnFiles[0])}.");
        }

        // Single .csproj in root (no .sln).
        var csprojFiles = Directory.GetFiles(repoRoot, "*.csproj", SearchOption.TopDirectoryOnly);
        if (csprojFiles.Length == 1)
        {
            var proj = QuotePath(Path.GetFileName(csprojFiles[0]));
            return new DetectedCommands(
                InitCommand: null,
                RestoreCommand: $"dotnet restore {proj}",
                BuildCommand: $"dotnet build {proj}",
                TestCommand: $"dotnet test {proj}",
                Ecosystem: "dotnet",
                Ambiguous: false,
                AmbiguityDetail: null);
        }

        // ── 2. Node.js / npm ─────────────────────────────────────────
        if (File.Exists(Path.Combine(repoRoot, "package.json")))
        {
            var hasBuild = false;
            var hasTest = false;
            try
            {
                var json = File.ReadAllText(Path.Combine(repoRoot, "package.json"));
                var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("scripts", out var scripts))
                {
                    hasBuild = scripts.TryGetProperty("build", out _);
                    hasTest = scripts.TryGetProperty("test", out _);
                }
            }
            catch { /* malformed — best effort */ }

            return new DetectedCommands(
                InitCommand: null,
                RestoreCommand: "npm install",
                BuildCommand: hasBuild ? "npm run build" : null,
                TestCommand: hasTest ? "npm test" : null,
                Ecosystem: "node",
                Ambiguous: false,
                AmbiguityDetail: null);
        }

        // ── 3. Rust / Cargo ──────────────────────────────────────────
        if (File.Exists(Path.Combine(repoRoot, "Cargo.toml")))
        {
            return new DetectedCommands(
                InitCommand: null,
                RestoreCommand: null,
                BuildCommand: "cargo build",
                TestCommand: "cargo test",
                Ecosystem: "rust",
                Ambiguous: false,
                AmbiguityDetail: null);
        }

        // ── 4. Go ────────────────────────────────────────────────────
        if (File.Exists(Path.Combine(repoRoot, "go.mod")))
        {
            return new DetectedCommands(
                InitCommand: null,
                RestoreCommand: "go mod download",
                BuildCommand: "go build ./...",
                TestCommand: "go test ./...",
                Ecosystem: "go",
                Ambiguous: false,
                AmbiguityDetail: null);
        }

        // ── 5. Java / Maven ─────────────────────────────────────────
        if (File.Exists(Path.Combine(repoRoot, "pom.xml")))
        {
            return new DetectedCommands(
                InitCommand: null,
                RestoreCommand: null,
                BuildCommand: "mvn package -DskipTests",
                TestCommand: "mvn test",
                Ecosystem: "maven",
                Ambiguous: false,
                AmbiguityDetail: null);
        }

        // ── 6. Python ────────────────────────────────────────────────
        if (File.Exists(Path.Combine(repoRoot, "pyproject.toml")))
        {
            return new DetectedCommands(
                InitCommand: null,
                RestoreCommand: "pip install -e .",
                BuildCommand: null,
                TestCommand: "pytest",
                Ecosystem: "python",
                Ambiguous: false,
                AmbiguityDetail: null);
        }
        if (File.Exists(Path.Combine(repoRoot, "requirements.txt")))
        {
            return new DetectedCommands(
                InitCommand: null,
                RestoreCommand: "pip install -r requirements.txt",
                BuildCommand: null,
                TestCommand: "pytest",
                Ecosystem: "python",
                Ambiguous: false,
                AmbiguityDetail: null);
        }

        return Unknown();
    }

    /// <summary>
    /// Merges detected commands into an existing repo row, only setting
    /// fields that are currently null/empty (operator overrides are preserved).
    /// Returns <c>true</c> if any field was updated.
    /// </summary>
    public static bool MergeInto(Persistence.RepoRow row, DetectedCommands detected)
    {
        var changed = false;
        if (string.IsNullOrEmpty(row.InitCommand) && !string.IsNullOrEmpty(detected.InitCommand))
        {
            row.InitCommand = detected.InitCommand;
            changed = true;
        }
        if (string.IsNullOrEmpty(row.RestoreCommand) && !string.IsNullOrEmpty(detected.RestoreCommand))
        {
            row.RestoreCommand = detected.RestoreCommand;
            changed = true;
        }
        if (string.IsNullOrEmpty(row.BuildCommand) && !string.IsNullOrEmpty(detected.BuildCommand))
        {
            row.BuildCommand = detected.BuildCommand;
            changed = true;
        }
        if (string.IsNullOrEmpty(row.TestCommand) && !string.IsNullOrEmpty(detected.TestCommand))
        {
            row.TestCommand = detected.TestCommand;
            changed = true;
        }
        return changed;
    }

    private static DetectedCommands Unknown() => new(
        null, null, null, null, "unknown", false, null);

    /// <summary>
    /// Wraps a filename in double quotes if it contains spaces, so the
    /// generated command string survives <see cref="CrewQueueWorker.ParseCommandLine"/>.
    /// </summary>
    private static string QuotePath(string name) =>
        name.Contains(' ') ? $"\"{name}\"" : name;
}
