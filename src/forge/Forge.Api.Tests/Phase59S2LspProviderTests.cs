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
///         files in the picked language AND no foreign source code
///         (docs / config only).</item>
///   <item>Resolve returns null (defers) when the diff carries
///         source code in another language (e.g. an all-C# diff in a
///         repo that also has a tsconfig.json) — it must not skip the
///         gate on files it never examined.</item>
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
    public async Task Resolve_ReturnsNull_WhenDiffIsForeignSourceCode()
    {
        // Repro of the operator bug (2026-05-20): a workspace that
        // looks like TypeScript (tsconfig.json present) but whose diff
        // is ENTIRELY C# (the UpdateService integration-test
        // migration). The LSP provider must NOT terminate the chain
        // with a Skipped result — it never examined the .cs files. It
        // returns null so the chain falls through to the C# heuristic,
        // which finds the changed test projects and runs the gate.
        var root = Path.Combine(Path.GetTempPath(), "phase59s2-prov-foreign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "tsconfig.json"), "{}");

        await using var provider = new LspImpactedTestProvider();
        var result = await provider.ResolveAsync(
            workspace: root,
            changedFiles: new[] { "src/UpdateService/UpdateServiceIntegrationTests.cs" },
            originalCommand: "dotnet test",
            ct: CancellationToken.None);

        Assert.Null(result); // defers to the next provider, never skips
    }

    [Fact]
    public async Task Resolve_StillSkips_ForDocAndConfigOnlyDiff()
    {
        // Guard against over-correction: a diff with NO source code in
        // any language (docs + config only) is still a legitimate skip
        // — there are genuinely no tests to run, and there is no
        // foreign source for another provider to rescue.
        var root = Path.Combine(Path.GetTempPath(), "phase59s2-prov-docs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "tsconfig.json"), "{}");

        await using var provider = new LspImpactedTestProvider();
        var result = await provider.ResolveAsync(
            workspace: root,
            changedFiles: new[] { "README.md", "docs/notes.txt", ".gitignore" },
            originalCommand: "npm test",
            ct: CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Skipped);
        Assert.Equal("lsp", result.Mode);
    }

    [Fact]
    public async Task Chain_FallsThroughToHeuristic_ForCSharpDiffInPolyglotRepo()
    {
        // End-to-end proof for the bug fix: the production chain shape
        // [LSP, Heuristic] (Roslyn omitted to simulate its MSBuild
        // load failure — exactly the screenshot's `mode: lsp` path).
        // A repo that detects as TypeScript (tsconfig.json) but whose
        // diff changes a C# test file must end up RUNNING the gate via
        // the heuristic, not skipping it.
        var root = Path.Combine(Path.GetTempPath(), "phase59s2-chain-cs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        Directory.CreateDirectory(Path.Combine(root, "other"));
        File.WriteAllText(Path.Combine(root, "tsconfig.json"), "{}");
        const string csproj = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>";
        // Impacted test project (the migrated UpdateService tests live here).
        File.WriteAllText(Path.Combine(root, "tests", "Foo.Tests.csproj"), csproj);
        File.WriteAllText(Path.Combine(root, "tests", "FooTests.cs"), "// migrated tests");
        // A second, unrelated test project so the impacted set stays
        // below the 80% whole-suite threshold and we get a real rewrite.
        File.WriteAllText(Path.Combine(root, "other", "Bar.Tests.csproj"), csproj);

        var chain = new ImpactedTestResolverChain(new IImpactedTestProvider[]
        {
            new LspImpactedTestProvider(),
            new HeuristicImpactedTestProvider(),
        });

        var result = await chain.ResolveAsync(
            workspace: root,
            changedFiles: new[] { "tests/FooTests.cs" },
            originalCommand: "dotnet test",
            ct: CancellationToken.None);

        Assert.False(result.Skipped);             // gate is NOT skipped
        Assert.Equal("heuristic", result.Mode);   // LSP deferred to the heuristic
        Assert.True(result.Rewritten);
        Assert.Contains("tests/Foo.Tests.csproj", result.ImpactedProjects);
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
