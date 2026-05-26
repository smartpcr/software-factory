// -----------------------------------------------------------------------
// <copyright file="ILspLanguageSupport.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories.ImpactedTests.Lsp;

/// <summary>
/// Phase 59 Stage S2 — per-language plug-in for
/// <see cref="LspImpactedTestProvider"/>. Each implementation owns
/// the four pieces that differ across language ecosystems:
/// <list type="bullet">
///   <item><see cref="LanguageId"/> + <see cref="DetectAsync"/> — does
///         this workspace look like {language}? (probes
///         <c>Cargo.toml</c> for Rust, <c>go.mod</c> for Go, etc.)</item>
///   <item><see cref="ServerProgram"/> + <see cref="ServerArgs"/> — what
///         to spawn on PATH (rust-analyzer, gopls, pylsp,
///         typescript-language-server).</item>
///   <item><see cref="SourceFileExtensions"/> — which file extensions
///         to consider as source (used to filter the changed-files
///         list before opening them in the LSP server).</item>
///   <item><see cref="IsTestReference"/> — given a reference location
///         (file + 0-indexed line/character) and the surrounding
///         source lines, decide whether the reference is INSIDE a
///         test entry point per this language's test framework
///         convention.</item>
///   <item><see cref="RewriteCommand"/> — produce the narrowed test
///         command (e.g. <c>cargo test -p foo --test bar</c>,
///         <c>go test -run '^TestA$' ./pkg/...</c>,
///         <c>pytest path/test_x.py::test_y</c>,
///         <c>npx jest -t 'name'</c>) from the matched test set.</item>
/// </list>
/// </summary>
public interface ILspLanguageSupport
{
    /// <summary>Short identifier — `"rust"`, `"go"`, `"python"`, `"typescript"`.</summary>
    string LanguageId { get; }

    /// <summary>
    /// Cheap probe — does this workspace look like a {language}
    /// project? Implementations check for marker files (e.g.
    /// <c>Cargo.toml</c>, <c>go.mod</c>, <c>pyproject.toml</c>,
    /// <c>tsconfig.json</c>). Returns false on IO error.
    /// </summary>
    Task<bool> DetectAsync(string workspace, CancellationToken ct);

    /// <summary>
    /// LSP server executable name as it appears on PATH
    /// (<c>rust-analyzer</c>, <c>gopls</c>, etc.). The
    /// <see cref="MinimalLspClient"/> ctor wraps a missing-server
    /// failure as <see cref="LspServerUnavailableException"/>; the
    /// provider catches it + falls through to the next chain entry.
    /// </summary>
    string ServerProgram { get; }

    /// <summary>
    /// Extra command-line args passed to the server. Most servers
    /// take none; <c>typescript-language-server</c> needs
    /// <c>--stdio</c>.
    /// </summary>
    IReadOnlyList<string> ServerArgs { get; }

    /// <summary>
    /// Lowercase source-file extensions (with the dot —
    /// <c>".rs"</c>, <c>".go"</c>, <c>".py"</c>, <c>".ts"</c>,
    /// <c>".tsx"</c>). Used to skip changed files the LSP server
    /// won't understand.
    /// </summary>
    IReadOnlyList<string> SourceFileExtensions { get; }

    /// <summary>
    /// Decide whether the given reference location is INSIDE a
    /// test entry point. The implementation receives the source
    /// lines of the referenced file (already loaded by the
    /// provider) + the 0-indexed line / character of the reference
    /// + the file path (some languages care: Go tests must live in
    /// <c>*_test.go</c>).
    ///
    /// <para>When this returns a non-null
    /// <see cref="TestEntryPoint"/>, the test is added to the
    /// matched set and the entry point's fully-qualified name +
    /// owning package/module are stamped for the rewrite step.</para>
    /// </summary>
    TestEntryPoint? IsTestReference(string filePath, IReadOnlyList<string> sourceLines, int line, int character);

    /// <summary>
    /// Build the narrowed test command from the matched test entry
    /// points. Receives the original command (e.g. <c>"cargo test"</c>)
    /// and the matched set, returns the rewritten command. Each
    /// language has its own syntax for filtering tests; this is
    /// where it gets centralised.
    /// </summary>
    string RewriteCommand(string originalCommand, string workspace, IReadOnlyList<TestEntryPoint> impacted);

    /// <summary>
    /// When the server found NO impacted tests, whether to DEFER (the
    /// provider returns <c>null</c> so the next chain provider gets a
    /// turn) instead of SKIP (terminate the chain — the gate runs
    /// nothing). Defaults to <c>false</c>: the trusted servers
    /// (rust-analyzer / gopls / pylsp / tsserver) treat an empty match as
    /// a genuine "no impacted tests → safe to skip". C# overrides this to
    /// <c>true</c> — <c>csharp-ls</c> on large sln-less traversal repos
    /// can return empty because it couldn't load the workspace, and
    /// skipping the gate there would be a false green; deferring lets
    /// Roslyn / the heuristic still run.
    /// </summary>
    bool DeferGateWhenNoImpactedFound => false;
}

/// <summary>
/// A test function the LSP detector matched. <see cref="OwningPath"/>
/// is the path the test runner targets (package dir for Go, file path
/// for pytest / jest, crate name for cargo). <see cref="TestName"/> is
/// the bare test identifier (function name for Rust/Go/Python, the
/// <c>it()/test()</c> title string for TypeScript).
/// </summary>
public sealed record TestEntryPoint(
    string FilePath,
    string OwningPath,
    string TestName,
    string LanguageId);
