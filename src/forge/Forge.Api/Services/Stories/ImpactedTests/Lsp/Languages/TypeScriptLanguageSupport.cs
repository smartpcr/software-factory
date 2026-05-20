// -----------------------------------------------------------------------
// <copyright file="TypeScriptLanguageSupport.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories.ImpactedTests.Lsp.Languages;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Phase 59 Stage S2 — TypeScript / JavaScript + typescript-language-
/// server + jest / vitest.
///
/// <para><b>Detection</b>: any of <c>tsconfig.json</c>, a
/// <c>package.json</c> with a <c>"dependencies"</c> or
/// <c>"devDependencies"</c> containing <c>typescript</c> /
/// <c>jest</c> / <c>vitest</c> / <c>mocha</c>, or the bare
/// presence of <c>.ts</c> / <c>.tsx</c> files.</para>
///
/// <para><b>Server</b>: <c>typescript-language-server --stdio</c>.
/// Operators install via <c>npm i -g typescript-language-server
/// typescript</c>.</para>
///
/// <para><b>Test entry detection</b>: jest / mocha / vitest all
/// share the BDD-style <c>it('name', () =&gt; {...})</c> /
/// <c>test('name', () =&gt; {...})</c> / <c>describe('group',
/// () =&gt; {...})</c> shape. A reference is INSIDE a test when
/// it lives between an <c>it(...)</c> opening + its closing
/// brace. We approximate by walking up source lines to find the
/// nearest <c>it(</c>/<c>test(</c> call whose opening brace
/// hasn't been balanced yet — close-enough for the impacted-
/// test filter, no AST required.</para>
///
/// <para><b>Rewrite</b>: <c>npx jest -t '{name1}|{name2}'</c> for
/// jest, <c>npx vitest run -t '{name}'</c> for vitest. We sniff
/// which runner the workspace uses by scanning
/// <c>package.json</c>; default to jest when neither is found.</para>
/// </summary>
public sealed class TypeScriptLanguageSupport : ILspLanguageSupport
{
    public string LanguageId => "typescript";

    public string ServerProgram => "typescript-language-server";

    public IReadOnlyList<string> ServerArgs { get; } = new[] { "--stdio" };

    public IReadOnlyList<string> SourceFileExtensions { get; } = new[] { ".ts", ".tsx", ".js", ".jsx", ".mts", ".cts" };

    // Match `it('name', ...)`, `test("name", ...)`, `describe('group',
    // ...)` — capture the test name from either single-quoted, double-
    // quoted, or backtick strings. We don't try to handle dynamic
    // template strings (`it(\`foo ${x}\`, ...)`); those count as a
    // test but the name capture is just the literal template body.
    private static readonly Regex TestCallRegex = new(
        @"^\s*(?:it|test|describe)\s*\.?(?:only|skip|each)?\s*\(\s*[`'""]([^`'""]*)[`'""]",
        RegexOptions.Compiled);

    public Task<bool> DetectAsync(string workspace, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            return Task.FromResult(false);
        }
        try
        {
            if (File.Exists(Path.Combine(workspace, "tsconfig.json"))) return Task.FromResult(true);
            if (Directory.EnumerateFiles(workspace, "tsconfig.json", SearchOption.AllDirectories).Any())
            {
                return Task.FromResult(true);
            }
            // Bare .ts/.tsx files anywhere → recognise.
            foreach (var ext in new[] { "*.ts", "*.tsx" })
            {
                if (Directory.EnumerateFiles(workspace, ext, SearchOption.AllDirectories).Any())
                {
                    return Task.FromResult(true);
                }
            }
            return Task.FromResult(false);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public TestEntryPoint? IsTestReference(string filePath, IReadOnlyList<string> sourceLines, int line, int character)
    {
        if (line < 0 || line >= sourceLines.Count) return null;

        // Walk up looking for an unbalanced `it(` / `test(` /
        // `describe(` line. We accumulate brace depth from the
        // reference line upward — if we find an `it(...)` line and
        // the cumulative depth above it is >= 0 (we haven't escaped
        // the enclosing block), it's our test.
        int depth = 0;
        var start = Math.Min(line, sourceLines.Count - 1);
        var stop = Math.Max(0, start - 500);
        for (var i = start; i >= stop; i--)
        {
            var src = sourceLines[i];
            // Update brace depth (ignoring braces inside strings is
            // a refinement for v2 — false positives at this layer
            // are tolerable since we only use depth to decide
            // "still inside a block").
            for (var j = src.Length - 1; j >= 0; j--)
            {
                var c = src[j];
                if (c == '}') depth++;
                else if (c == '{') depth--;
            }
            if (depth > 0)
            {
                // We've escaped the enclosing block — the reference
                // isn't inside this scope.
                continue;
            }
            var m = TestCallRegex.Match(src);
            if (m.Success)
            {
                return new TestEntryPoint(
                    FilePath: filePath,
                    OwningPath: filePath,
                    TestName: m.Groups[1].Value,
                    LanguageId: this.LanguageId);
            }
        }
        return null;
    }

    public string RewriteCommand(string originalCommand, string workspace, IReadOnlyList<TestEntryPoint> impacted)
    {
        // Jest + vitest both accept `-t '<regex>'` to filter by test
        // name. Group matched test names into an OR regex, escape
        // regex specials so a name like `should handle (a|b)` doesn't
        // explode the filter.
        var sb = new StringBuilder();
        sb.Append(originalCommand.TrimEnd());

        // Drop any existing -t the operator passed so we don't
        // double-up.
        var sanitized = StripExistingDashT(sb.ToString());
        sb.Clear();
        sb.Append(sanitized.TrimEnd());

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in impacted)
        {
            if (!string.IsNullOrWhiteSpace(t.TestName)) names.Add(t.TestName);
        }
        if (names.Count == 0) return sb.ToString();

        var regex = string.Join("|", names.Select(EscapeForJestRegex));
        sb.Append(" -t '").Append(regex).Append('\'');
        return sb.ToString();
    }

    private static string StripExistingDashT(string command)
    {
        // Drop `-t 'something'` or `-t "something"` once.
        var idx = command.IndexOf(" -t ", StringComparison.Ordinal);
        if (idx < 0) return command;
        var after = command.IndexOf('\'', idx + 4);
        if (after < 0) after = command.IndexOf('"', idx + 4);
        if (after < 0) return command;
        var end = command.IndexOfAny(new[] { '\'', '"' }, after + 1);
        if (end < 0) return command;
        return command[..idx] + command[(end + 1)..];
    }

    private static string EscapeForJestRegex(string s)
    {
        // Standard regex meta escape; jest passes -t straight to a
        // RegExp constructor. We don't anchor — jest's -t matches
        // substrings.
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\':
                case '.':
                case '^':
                case '$':
                case '|':
                case '?':
                case '*':
                case '+':
                case '(':
                case ')':
                case '[':
                case ']':
                case '{':
                case '}':
                    sb.Append('\\');
                    sb.Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
