// -----------------------------------------------------------------------
// <copyright file="GoLanguageSupport.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories.ImpactedTests.Lsp.Languages;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Phase 59 Stage S2 — Go + gopls + the go test runner.
///
/// <para><b>Detection</b>: a <c>go.mod</c> at the workspace root or
/// at any depth.</para>
///
/// <para><b>Server</b>: <c>gopls</c> on PATH (the canonical Go
/// language server, <c>go install golang.org/x/tools/gopls@latest</c>).</para>
///
/// <para><b>Test entry detection</b>: Go's stdlib test framework is
/// strict — a test is any function declared as
/// <c>func TestXxx(t *testing.T)</c>, AND the file MUST end in
/// <c>_test.go</c>. Benchmarks (<c>BenchmarkXxx(b *testing.B)</c>)
/// and examples (<c>ExampleXxx()</c>) are intentionally excluded
/// here — they're not run by default with <c>go test</c>'s default
/// flags + would inflate the matched set without value.</para>
///
/// <para><b>Rewrite</b>: <c>go test ./{pkg}/... -run '^TestA$|^TestB$'</c>.
/// Group matched tests by owning package directory (parent dir of
/// the test file); emit one <c>-run</c> regex per invocation. When
/// matched tests span multiple packages, we widen to <c>./...</c>
/// and let the <c>-run</c> regex narrow.</para>
/// </summary>
public sealed class GoLanguageSupport : ILspLanguageSupport
{
    public string LanguageId => "go";

    public string ServerProgram => "gopls";

    public IReadOnlyList<string> ServerArgs { get; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceFileExtensions { get; } = new[] { ".go" };

    // `func TestXxx(t *testing.T)` — Go's stdlib testing convention.
    // Allow whitespace + optional leading underscore in `Test_xxx`
    // (Go style guide discourages but valid). Captures the test name.
    private static readonly Regex TestFnRegex = new(
        @"^\s*func\s+(Test[\w_]*)\s*\(\s*\w+\s+\*testing\.T\s*\)",
        RegexOptions.Compiled);

    public Task<bool> DetectAsync(string workspace, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            return Task.FromResult(false);
        }
        try
        {
            if (File.Exists(Path.Combine(workspace, "go.mod"))) return Task.FromResult(true);
            return Task.FromResult(Directory.EnumerateFiles(workspace, "go.mod", SearchOption.AllDirectories).Any());
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public TestEntryPoint? IsTestReference(string filePath, IReadOnlyList<string> sourceLines, int line, int character)
    {
        // Hard requirement: file must end in `_test.go`. A reference
        // in a non-test file isn't a test entry point even if the
        // surrounding fn happens to match the regex.
        if (!filePath.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase)) return null;
        if (line < 0 || line >= sourceLines.Count) return null;

        // Walk up to find the enclosing `func TestXxx(t *testing.T)`.
        // Go funcs are typically short; cap at 500 lines for safety.
        var start = Math.Min(line, sourceLines.Count - 1);
        var stop = Math.Max(0, start - 500);
        for (var i = start; i >= stop; i--)
        {
            var m = TestFnRegex.Match(sourceLines[i]);
            if (m.Success)
            {
                var pkgDir = Path.GetDirectoryName(filePath) ?? string.Empty;
                return new TestEntryPoint(
                    FilePath: filePath,
                    OwningPath: pkgDir,
                    TestName: m.Groups[1].Value,
                    LanguageId: this.LanguageId);
            }
            // If we hit another top-level `func ` declaration that
            // ISN'T a test, the reference is inside a non-test
            // helper — bail.
            if (sourceLines[i].StartsWith("func ", StringComparison.Ordinal))
            {
                return null;
            }
        }
        return null;
    }

    public string RewriteCommand(string originalCommand, string workspace, IReadOnlyList<TestEntryPoint> impacted)
    {
        // Group by owning package directory. If everything's in one
        // package, target that package; otherwise widen to ./... and
        // rely on -run to narrow.
        var byPkg = impacted
            .GroupBy(t => t.OwningPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var pkgArg = byPkg.Count == 1
            ? this.PackageRel(workspace, byPkg[0].Key) + "/..."
            : "./...";
        var runRegex = BuildRunRegex(impacted);

        // Tear off the existing `./...` if the operator's command
        // already targets the workspace, then re-append our scope.
        var tokens = originalCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        // `go test` (possibly with operator-supplied flags) + our scope + -run filter
        foreach (var tok in tokens)
        {
            if (tok == "./..." || tok.StartsWith("./", StringComparison.Ordinal)) continue;
            if (tok.StartsWith("-run", StringComparison.Ordinal)) continue;
            sb.Append(tok);
            sb.Append(' ');
        }
        sb.Append(pkgArg);
        sb.Append(" -run '");
        sb.Append(runRegex);
        sb.Append('\'');
        return sb.ToString().Trim();
    }

    private string PackageRel(string workspace, string pkgDir)
    {
        try
        {
            var rel = Path.GetRelativePath(workspace, pkgDir).Replace(Path.DirectorySeparatorChar, '/');
            return rel == "." ? "./" : "./" + rel;
        }
        catch
        {
            return "./...";
        }
    }

    private static string BuildRunRegex(IReadOnlyList<TestEntryPoint> impacted)
    {
        // Anchor each test name so -run doesn't accidentally match
        // longer names that contain the matched fn name as a
        // substring.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var t in impacted)
        {
            if (!seen.Add(t.TestName)) continue;
            if (sb.Length > 0) sb.Append('|');
            sb.Append('^').Append(t.TestName).Append('$');
        }
        return sb.ToString();
    }
}
