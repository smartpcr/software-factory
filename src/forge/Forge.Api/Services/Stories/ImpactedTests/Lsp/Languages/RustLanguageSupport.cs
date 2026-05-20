// -----------------------------------------------------------------------
// <copyright file="RustLanguageSupport.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories.ImpactedTests.Lsp.Languages;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Phase 59 Stage S2 — Rust + rust-analyzer + Cargo.
///
/// <para><b>Detection</b>: any <c>Cargo.toml</c> under the
/// workspace (top-level for a single crate, or under
/// <c>members = […]</c> for a workspace).</para>
///
/// <para><b>Server</b>: <c>rust-analyzer</c> on PATH. (<c>rustup
/// component add rust-analyzer</c> is the canonical install path;
/// most developer machines have it.)</para>
///
/// <para><b>Test entry detection</b>: walk source lines backward
/// from the reference's enclosing <c>fn</c> declaration. A test
/// function carries one of:</para>
/// <list type="bullet">
///   <item><c>#[test]</c> — standard libtest</item>
///   <item><c>#[tokio::test]</c> / <c>#[async_std::test]</c> /
///         <c>#[actix_web::test]</c> — async-runtime variants</item>
/// </list>
///
/// <para><b>Rewrite</b>: <c>cargo test {test_path}</c> where each
/// test path is the test function's module-qualified name. cargo
/// honours space-separated test filters, so we list each matched
/// test by its FQN — cargo runs only tests whose name matches at
/// least one prefix.</para>
/// </summary>
public sealed class RustLanguageSupport : ILspLanguageSupport
{
    public string LanguageId => "rust";

    public string ServerProgram => "rust-analyzer";

    public IReadOnlyList<string> ServerArgs { get; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceFileExtensions { get; } = new[] { ".rs" };

    // Attribute names that mark a fn as a test. The leaf identifier
    // is checked — `tokio::test` → `test`, `actix_web::test` → `test`,
    // bare `test` → `test`. We also allow `should_panic` since it
    // implies #[test] in practice.
    private static readonly HashSet<string> TestAttributeLeaves = new(StringComparer.Ordinal)
    {
        "test", "should_panic",
    };

    // Match `#[xxx]` or `#[xxx::yyy]` or `#[xxx(...)]` attribute lines.
    private static readonly Regex AttributeRegex = new(
        @"^\s*#\[([\w:]+)(?:\s*\(.*?\))?\s*\]\s*$",
        RegexOptions.Compiled);

    // Match `fn name(` or `pub fn name(` or `async fn name(` —
    // capture the fn name. Doesn't try to handle multi-line decls;
    // rust-analyzer's reference locations land on the simple form
    // overwhelmingly.
    private static readonly Regex FnDeclRegex = new(
        @"^\s*(?:pub(?:\s*\([^)]*\))?\s+)?(?:async\s+)?(?:unsafe\s+)?(?:const\s+)?(?:extern(?:\s+""[^""]*"")?\s+)?fn\s+(\w+)\s*[<(]",
        RegexOptions.Compiled);

    public Task<bool> DetectAsync(string workspace, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            return Task.FromResult(false);
        }
        try
        {
            return Task.FromResult(File.Exists(Path.Combine(workspace, "Cargo.toml"))
                || Directory.EnumerateFiles(workspace, "Cargo.toml", SearchOption.AllDirectories).Any());
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public TestEntryPoint? IsTestReference(string filePath, IReadOnlyList<string> sourceLines, int line, int character)
    {
        if (line < 0 || line >= sourceLines.Count) return null;

        // Find the enclosing `fn` declaration by walking up from the
        // reference line. We accept anything within 200 lines (a real
        // function shouldn't be longer in practice).
        var startSearch = Math.Min(line, sourceLines.Count - 1);
        var stopAt = Math.Max(0, startSearch - 200);

        int fnLine = -1;
        string? fnName = null;
        for (var i = startSearch; i >= stopAt; i--)
        {
            var m = FnDeclRegex.Match(sourceLines[i]);
            if (m.Success)
            {
                fnLine = i;
                fnName = m.Groups[1].Value;
                break;
            }
        }
        if (fnLine < 0 || fnName is null) return null;

        // Walk further up collecting attributes immediately above the
        // fn (allow whitespace + comments interleaved). Stop on the
        // first non-attribute, non-blank, non-comment line.
        for (var i = fnLine - 1; i >= 0 && i >= fnLine - 10; i--)
        {
            var raw = sourceLines[i];
            var trimmed = raw.TrimStart();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("/*", StringComparison.Ordinal)) continue;
            var am = AttributeRegex.Match(raw);
            if (!am.Success)
            {
                // Not an attribute — stop scanning; we're past the
                // attribute block.
                break;
            }
            var name = am.Groups[1].Value;
            var leaf = name.Contains("::", StringComparison.Ordinal)
                ? name[(name.LastIndexOf("::", StringComparison.Ordinal) + 2)..]
                : name;
            if (TestAttributeLeaves.Contains(leaf))
            {
                // FQN approximation: just the fn name. cargo's
                // filter takes substrings so listing the bare fn
                // name matches `module::tests::fn_name` too.
                return new TestEntryPoint(
                    FilePath: filePath,
                    OwningPath: filePath, // cargo doesn't take per-test file paths
                    TestName: fnName,
                    LanguageId: this.LanguageId);
            }
        }
        return null;
    }

    public string RewriteCommand(string originalCommand, string workspace, IReadOnlyList<TestEntryPoint> impacted)
    {
        // `cargo test` with no project arg + space-separated filter
        // names runs the whole workspace + matches tests whose name
        // contains any filter as substring. We list each matched
        // fn name as a filter.
        var sb = new StringBuilder();
        sb.Append(originalCommand.TrimEnd());
        if (!originalCommand.Contains(" -- ", StringComparison.Ordinal))
        {
            sb.Append(" --");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in impacted)
        {
            if (seen.Add(t.TestName))
            {
                sb.Append(' ');
                sb.Append(t.TestName);
            }
        }
        return sb.ToString();
    }
}
