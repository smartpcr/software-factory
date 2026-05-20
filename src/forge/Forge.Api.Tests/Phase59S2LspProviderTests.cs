// -----------------------------------------------------------------------
// <copyright file="Phase59S2LspProviderTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories.ImpactedTests;
using Forge.Api.Services.Stories.ImpactedTests.Lsp;
using Xunit;

/// <summary>
/// Phase 59 Stage S2 — provider-level contract for
/// <see cref="LspImpactedTestProvider"/>. Covers the cases that
/// don't require a live LSP server on PATH:
/// <list type="bullet">
///   <item>CanHandle picks the first language whose marker file is
///         present.</item>
///   <item>Resolve returns null when the server isn't on PATH
///         (chain falls through to heuristic).</item>
///   <item>Resolve returns Skipped when the diff has no source
///         files in the picked language.</item>
/// </list>
///
/// <para>Live integration against rust-analyzer / gopls / pylsp /
/// typescript-language-server is a separate harness — same
/// skip-on-missing pattern as the Roslyn integration tests
/// (<see cref="Phase59CRoslynImpactedTestsTests"/>).</para>
/// </summary>
public class Phase59S2LspProviderTests
{
    [Fact]
    public async Task CanHandle_ReturnsTrue_ForRustWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "phase59s2-prov-rust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Cargo.toml"), "[package]\nname = \"x\"");

        await using var provider = new LspImpactedTestProvider();
        Assert.True(await provider.CanHandleAsync(root, CancellationToken.None));
    }

    [Fact]
    public async Task CanHandle_ReturnsTrue_ForGoWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "phase59s2-prov-go-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "go.mod"), "module x\ngo 1.21");

        await using var provider = new LspImpactedTestProvider();
        Assert.True(await provider.CanHandleAsync(root, CancellationToken.None));
    }

    [Fact]
    public async Task CanHandle_ReturnsTrue_ForPythonWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "phase59s2-prov-py-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "pyproject.toml"), "[project]\nname=\"x\"");

        await using var provider = new LspImpactedTestProvider();
        Assert.True(await provider.CanHandleAsync(root, CancellationToken.None));
    }

    [Fact]
    public async Task CanHandle_ReturnsTrue_ForTypeScriptWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "phase59s2-prov-ts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "tsconfig.json"), "{}");

        await using var provider = new LspImpactedTestProvider();
        Assert.True(await provider.CanHandleAsync(root, CancellationToken.None));
    }

    [Fact]
    public async Task CanHandle_ReturnsFalse_ForBareWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "phase59s2-prov-bare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "README.md"), "# nothing here");

        await using var provider = new LspImpactedTestProvider();
        Assert.False(await provider.CanHandleAsync(root, CancellationToken.None));
    }

    [Fact]
    public async Task Resolve_ReturnsSkipped_WhenDiffHasNoSourceFiles()
    {
        // Rust workspace, but the diff only touched a markdown file.
        // Resolver should short-circuit before spawning the server.
        var root = Path.Combine(Path.GetTempPath(), "phase59s2-prov-skip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Cargo.toml"), "[package]\nname = \"x\"");
        File.WriteAllText(Path.Combine(root, "README.md"), "# rust crate");

        await using var provider = new LspImpactedTestProvider();
        var result = await provider.ResolveAsync(
            workspace: root,
            changedFiles: new[] { "README.md" },
            originalCommand: "cargo test",
            ct: CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Skipped);
        Assert.Equal("lsp", result.Mode);
        Assert.Contains("no rust source files", result.Reason);
    }

    [Fact]
    public async Task Resolve_ReturnsNull_WhenServerNotOnPath()
    {
        // Rust workspace + a real .rs file → provider tries to spawn
        // rust-analyzer. On a host without it, the spawn throws
        // LspServerUnavailableException + ResolveAsync returns null
        // so the chain falls through to the heuristic. We pin that
        // failure mode is soft (null, not exception).
        var root = Path.Combine(Path.GetTempPath(), "phase59s2-prov-noserver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Cargo.toml"), "[package]\nname = \"x\"");
        File.WriteAllText(Path.Combine(root, "src.rs"), "fn main() {}");

        // Inject a language support whose serverProgram is guaranteed
        // not to exist. CanHandle still wires via Cargo.toml so the
        // provider commits to LSP, then the spawn fails.
        await using var provider = new LspImpactedTestProvider(new ILspLanguageSupport[]
        {
            new MissingServerLanguageSupport(),
        });

        var result = await provider.ResolveAsync(
            workspace: root,
            changedFiles: new[] { "src.rs" },
            originalCommand: "cargo test",
            ct: CancellationToken.None);

        Assert.Null(result); // chain falls through
    }

    /// <summary>
    /// Test double: claims Cargo workspaces but its server program
    /// is guaranteed to fail (<c>__definitely_not_on_path__</c>).
    /// Used to assert the provider's soft-failure semantics.
    /// </summary>
    private sealed class MissingServerLanguageSupport : ILspLanguageSupport
    {
        public string LanguageId => "rust";
        public string ServerProgram => "__definitely_not_on_path__";
        public IReadOnlyList<string> ServerArgs { get; } = Array.Empty<string>();
        public IReadOnlyList<string> SourceFileExtensions { get; } = new[] { ".rs" };
        public Task<bool> DetectAsync(string workspace, CancellationToken ct) =>
            Task.FromResult(File.Exists(Path.Combine(workspace, "Cargo.toml")));
        public TestEntryPoint? IsTestReference(string filePath, IReadOnlyList<string> sourceLines, int line, int character) =>
            null;
        public string RewriteCommand(string originalCommand, string workspace, IReadOnlyList<TestEntryPoint> impacted) =>
            originalCommand;
    }
}
