// -----------------------------------------------------------------------
// <copyright file="CSharpLanguageSupport.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories.ImpactedTests.Lsp.Languages;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// C# + csharp-ls + the dotnet test runner. Lets the LSP impacted-test
/// path handle C# too, so every ecosystem narrows through the SAME
/// provider (previously C# was special-cased to the in-process
/// <see cref="Roslyn.RoslynImpactedTestProvider"/>). Routed here by
/// putting <see cref="LspImpactedTestProvider"/> first in the chain +
/// mapping the <c>dotnet</c> ecosystem to this support.
///
/// <para><b>Detection</b>: a <c>build.proj</c> / <c>.sln</c> at the root,
/// or any <c>.csproj</c> in the tree.</para>
///
/// <para><b>Server</b>: <c>csharp-ls</c> on PATH
/// (<c>dotnet tool install -g csharp-ls</c>). Auto-detects the solution /
/// projects in the workspace.</para>
///
/// <para><b>Test entry detection</b>: the reference's enclosing method
/// carries a test attribute (<c>[Fact]</c> / <c>[Theory]</c> /
/// <c>[Test]</c> / <c>[TestMethod]</c> / <c>[DataTestMethod]</c> /
/// <c>[TestCase]</c>). The fully-qualified name (<c>Namespace.Class.Method</c>)
/// is derived from the surrounding source so the rewrite can target it
/// precisely.</para>
///
/// <para><b>Rewrite</b>: <c>dotnet test {impacted.csproj} --filter
/// "(EXISTING)&amp;(FullyQualifiedName~A|FullyQualifiedName~B)"</c> when all
/// impacted tests live in one project (full narrowing — only that project
/// builds + runs). When they span multiple projects, <c>dotnet test</c>
/// only takes one target, so we keep the original target (e.g.
/// <c>build.proj</c>) and rely on the merged filter to narrow which tests
/// execute. Any pre-existing <c>--filter</c> (a standing category exclude
/// like <c>TestCategory!~CTQ</c>) is AND-merged, never clobbered.</para>
/// </summary>
public sealed class CSharpLanguageSupport : ILspLanguageSupport
{
    public string LanguageId => "csharp";

    public string ServerProgram => "csharp-ls";

    public IReadOnlyList<string> ServerArgs { get; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceFileExtensions { get; } = new[] { ".cs" };

    // csharp-ls can return zero references because it failed to load a
    // large sln-less workspace, not because there are genuinely no impacted
    // tests. Defer to Roslyn / the heuristic in that case rather than
    // skipping the gate (which would be a false green).
    public bool DeferGateWhenNoImpactedFound => true;

    // A method declaration: an access modifier, then (return type / async /
    // generics …) up to the method name + `(`. Deliberately requires an
    // access modifier so we don't match control-flow `if (` / `while (`.
    private static readonly Regex MethodDeclRegex = new(
        @"\b(?:public|internal|protected|private)\b[^;={}]*?\b([A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*\(",
        RegexOptions.Compiled);

    // xUnit / NUnit / MSTest test markers, with or without a namespace
    // qualifier (`[Xunit.Fact]`) and with or without args (`[TestCase(1)]`).
    private static readonly Regex TestAttrRegex = new(
        @"\[\s*(?:[\w.]+\.)?(Fact|Theory|Test|TestMethod|DataTestMethod|TestCase)\b",
        RegexOptions.Compiled);

    private static readonly Regex ClassDeclRegex = new(
        @"\b(?:class|record|struct)\s+([A-Za-z_]\w*)",
        RegexOptions.Compiled);

    private static readonly Regex NamespaceRegex = new(
        @"\bnamespace\s+([A-Za-z_][\w.]*)",
        RegexOptions.Compiled);

    public Task<bool> DetectAsync(string workspace, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            return Task.FromResult(false);
        }
        try
        {
            if (File.Exists(Path.Combine(workspace, "build.proj"))) return Task.FromResult(true);
            if (Directory.EnumerateFiles(workspace, "*.sln", SearchOption.TopDirectoryOnly).Any()) return Task.FromResult(true);
            return Task.FromResult(Directory.EnumerateFiles(workspace, "*.csproj", SearchOption.AllDirectories).Any());
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public TestEntryPoint? IsTestReference(string filePath, IReadOnlyList<string> sourceLines, int line, int character)
    {
        if (line < 0 || line >= sourceLines.Count) return null;

        // Walk up to the enclosing method declaration.
        var start = Math.Min(line, sourceLines.Count - 1);
        var stop = Math.Max(0, start - 400);
        var methodLine = -1;
        string? methodName = null;
        for (var i = start; i >= stop; i--)
        {
            var m = MethodDeclRegex.Match(sourceLines[i]);
            if (m.Success)
            {
                methodName = m.Groups[1].Value;
                methodLine = i;
                break;
            }
        }
        if (methodLine < 0 || methodName is null) return null;

        // The enclosing method must carry a test attribute. Attributes stack
        // on the method line itself or the lines just above it; stop at a
        // blank-line gap so we don't borrow a sibling member's attribute.
        var hasTestAttr = false;
        var attrStop = Math.Max(0, methodLine - 8);
        for (var i = methodLine; i >= attrStop; i--)
        {
            if (TestAttrRegex.IsMatch(sourceLines[i])) { hasTestAttr = true; break; }
            if (i < methodLine && sourceLines[i].Trim().Length == 0) break;
        }
        if (!hasTestAttr) return null;

        // Derive Namespace.Class.Method for the --filter (FullyQualifiedName~).
        var className = FindEnclosing(sourceLines, methodLine, ClassDeclRegex);
        var ns = FindEnclosing(sourceLines, methodLine, NamespaceRegex);
        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(ns)) parts.Add(ns!);
        if (!string.IsNullOrEmpty(className)) parts.Add(className!);
        parts.Add(methodName);
        var fqn = string.Join('.', parts);

        var owningProj = FindOwningProject(filePath) ?? string.Empty;
        return new TestEntryPoint(
            FilePath: filePath,
            OwningPath: owningProj,
            TestName: fqn,
            LanguageId: this.LanguageId);
    }

    public string RewriteCommand(string originalCommand, string workspace, IReadOnlyList<TestEntryPoint> impacted)
    {
        var fqns = impacted
            .Select(t => t.TestName)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (fqns.Count == 0) return originalCommand;

        var includeGroup = string.Join("|", fqns.Select(f => "FullyQualifiedName~" + f));

        // Preserve any standing --filter (e.g. category excludes) by AND-merging.
        var existingFilter = ExtractFilterValue(originalCommand);
        var mergedFilter = string.IsNullOrEmpty(existingFilter)
            ? includeGroup
            : $"({existingFilter})&({includeGroup})";

        // Target the single impacted project for full narrowing; keep the
        // original target when impacted tests span multiple projects (dotnet
        // test accepts only one target).
        var projects = impacted
            .Select(t => t.OwningPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var target = projects.Count == 1
            ? RelPath(workspace, projects[0])
            : ExtractTarget(originalCommand);

        var sb = new StringBuilder("dotnet test");
        if (!string.IsNullOrEmpty(target)) sb.Append(' ').Append(QuoteIfNeeded(target!));
        foreach (var flag in PassthroughTokens(originalCommand))
        {
            sb.Append(' ').Append(flag);
        }
        sb.Append(" --filter \"").Append(mergedFilter).Append('"');
        return sb.ToString();
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static string? FindEnclosing(IReadOnlyList<string> lines, int fromLine, Regex regex)
    {
        for (var i = Math.Min(fromLine, lines.Count - 1); i >= 0; i--)
        {
            var m = regex.Match(lines[i]);
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    private static string? FindOwningProject(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            while (!string.IsNullOrEmpty(dir))
            {
                var proj = Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (proj is not null) return proj;
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch
        {
            // best-effort
        }
        return null;
    }

    private static readonly Regex FilterRegex = new(
        "--filter\\s+(?:\"(?<q>[^\"]*)\"|(?<u>\\S+))",
        RegexOptions.Compiled);

    private static string? ExtractFilterValue(string command)
    {
        var m = FilterRegex.Match(command);
        if (!m.Success) return null;
        return m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["u"].Value;
    }

    /// <summary>The first non-flag token after <c>dotnet test</c> (the project /
    /// solution / traversal-proj the command originally targeted).</summary>
    private static string? ExtractTarget(string command)
    {
        var withoutFilter = FilterRegex.Replace(command, string.Empty);
        var toks = withoutFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // toks[0]=dotnet, toks[1]=test; the next non-flag token is the target.
        for (var i = 2; i < toks.Length; i++)
        {
            if (!toks[i].StartsWith('-')) return toks[i];
        }
        return null;
    }

    /// <summary>
    /// Tokens to carry through unchanged: everything after <c>dotnet test</c>
    /// except the original target and the <c>--filter</c> (both replaced).
    /// Keeps operator flags like <c>--configuration Release</c>,
    /// <c>--logger trx</c>, <c>--verbosity minimal</c>.
    /// </summary>
    private static IEnumerable<string> PassthroughTokens(string command)
    {
        var withoutFilter = FilterRegex.Replace(command, string.Empty);
        var toks = withoutFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var targetSeen = false;
        for (var i = 2; i < toks.Length; i++)
        {
            var tok = toks[i];
            if (!targetSeen && !tok.StartsWith('-'))
            {
                targetSeen = true; // drop the original target — we set our own
                continue;
            }
            yield return tok;
        }
    }

    private static string RelPath(string workspace, string absProjectPath)
    {
        try
        {
            var rel = Path.GetRelativePath(Path.GetFullPath(workspace), Path.GetFullPath(absProjectPath));
            return rel.Replace(Path.DirectorySeparatorChar, '/');
        }
        catch
        {
            return absProjectPath;
        }
    }

    private static string QuoteIfNeeded(string s)
    {
        foreach (var ch in s)
        {
            if (ch == ' ' || ch == '\t') return $"\"{s}\"";
        }
        return s;
    }
}
