// -----------------------------------------------------------------------
// <copyright file="WorkspaceEcosystemDetectorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services;
using Xunit;

/// <summary>
/// docs/forge/e2e.md Stage 4.3 — the single shared ecosystem detector
/// used by BOTH the qa-e2e generator (which test harness/lib to
/// scaffold) and the impacted-test LSP chain (which language server to
/// launch). These tests pin the two-tier behaviour: root fast-path,
/// then a monorepo-aware scored deep scan, then <c>unknown</c>.
/// </summary>
public class WorkspaceEcosystemDetectorTests
{
    private static string NewTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "wseco-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Theory]
    [InlineData("go.mod", "module x\ngo 1.21", "go")]
    [InlineData("Cargo.toml", "[package]\nname=\"x\"", "rust")]
    [InlineData("pyproject.toml", "[project]\nname=\"x\"", "python")]
    [InlineData("package.json", "{\"name\":\"x\"}", "node")]
    [InlineData("pom.xml", "<project/>", "maven")]
    public void Detect_RootMarker_ReturnsEcosystem(string fileName, string content, string expected)
    {
        var root = NewTempDir();
        File.WriteAllText(Path.Combine(root, fileName), content);
        Assert.Equal(expected, WorkspaceEcosystemDetector.Detect(root));
    }

    [Fact]
    public void Detect_BareWorkspace_ReturnsUnknown()
    {
        var root = NewTempDir();
        File.WriteAllText(Path.Combine(root, "README.md"), "# nothing");
        Assert.Equal(WorkspaceEcosystemDetector.Unknown, WorkspaceEcosystemDetector.Detect(root));
    }

    [Fact]
    public void Detect_NullOrMissing_ReturnsUnknown()
    {
        Assert.Equal(WorkspaceEcosystemDetector.Unknown, WorkspaceEcosystemDetector.Detect(null));
        Assert.Equal(WorkspaceEcosystemDetector.Unknown, WorkspaceEcosystemDetector.Detect("   "));
        Assert.Equal(
            WorkspaceEcosystemDetector.Unknown,
            WorkspaceEcosystemDetector.Detect(Path.Combine(Path.GetTempPath(), "wseco-missing-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void Detect_GoMonorepo_MarkerInSubtree_ReturnsGo()
    {
        // Reproduces smartpcr/code-intelligence: the repo root has no
        // marker; the only go.mod lives under services/agent-memory.
        // The previous root-only scan returned `unknown` → defaulted to C#.
        var root = NewTempDir();
        var svc = Path.Combine(root, "services", "agent-memory");
        Directory.CreateDirectory(svc);
        File.WriteAllText(Path.Combine(svc, "go.mod"), "module example.com/agent-memory\ngo 1.21");
        File.WriteAllText(Path.Combine(svc, "main.go"), "package main\nfunc main() {}");
        File.WriteAllText(Path.Combine(svc, "store.go"), "package main");

        Assert.Equal("go", WorkspaceEcosystemDetector.Detect(root));
    }

    [Fact]
    public void Detect_SourcePredominanceBreaksTies()
    {
        // A Go repo with a stray requirements.txt (marker tie 1:1) must
        // still resolve to `go` because .go source files predominate —
        // score = markers*1000 + sourceFiles.
        var root = NewTempDir();
        var pkg = Path.Combine(root, "pkg");
        Directory.CreateDirectory(pkg);
        File.WriteAllText(Path.Combine(pkg, "go.mod"), "module x\ngo 1.21");
        File.WriteAllText(Path.Combine(pkg, "requirements.txt"), "requests==2.0");
        foreach (var i in Enumerable.Range(0, 12))
        {
            File.WriteAllText(Path.Combine(pkg, $"f{i}.go"), "package x");
        }
        File.WriteAllText(Path.Combine(pkg, "util.py"), "x = 1");

        Assert.Equal("go", WorkspaceEcosystemDetector.Detect(root));
    }

    [Theory]
    [InlineData("go", "go")]
    [InlineData("rust", "rust")]
    [InlineData("python", "python")]
    [InlineData("node", "typescript")]
    [InlineData("dotnet", null)]
    [InlineData("maven", null)]
    [InlineData("unknown", null)]
    public void ToLspLanguageId_MapsEcosystemToServerLanguage(string ecosystem, string? expected)
    {
        Assert.Equal(expected, WorkspaceEcosystemDetector.ToLspLanguageId(ecosystem));
    }
}
