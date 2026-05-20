// -----------------------------------------------------------------------
// <copyright file="PythonLanguageSupport.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Services.Stories.ImpactedTests.Lsp.Languages;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Phase 59 Stage S2 — Python + pylsp + pytest.
///
/// <para><b>Detection</b>: any of <c>pyproject.toml</c>,
/// <c>setup.py</c>, <c>setup.cfg</c>, <c>requirements.txt</c>, or
/// the presence of any <c>.py</c> file under the workspace root.</para>
///
/// <para><b>Server</b>: <c>pylsp</c> (python-lsp-server) on PATH.
/// Operators install via <c>pip install python-lsp-server</c>.</para>
///
/// <para><b>Test entry detection</b>: pytest discovery convention —
/// any <c>def</c> whose name starts with <c>test_</c> (or
/// <c>Test</c> for class-style tests). The file basename typically
/// matches <c>test_*.py</c> or <c>*_test.py</c>; we don't enforce
/// the filename pattern because pytest's discovery is configurable
/// (<c>python_files</c> in <c>pyproject.toml</c>) and rejecting
/// non-conventional layouts would false-negative real test files.</para>
///
/// <para><b>Rewrite</b>: <c>pytest path/to/file.py::test_name</c>.
/// pytest accepts a space-separated list of node ids so we list each
/// matched (file, test) pair directly.</para>
/// </summary>
public sealed class PythonLanguageSupport : ILspLanguageSupport
{
    public string LanguageId => "python";

    public string ServerProgram => "pylsp";

    public IReadOnlyList<string> ServerArgs { get; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceFileExtensions { get; } = new[] { ".py" };

    // `def test_foo(...)` OR `def Test_xxx(...)` — pytest accepts
    // both. We DON'T match `async def` for tests because pytest-asyncio
    // requires a separate runner; that's a follow-up.
    private static readonly Regex TestDefRegex = new(
        @"^\s*(?:async\s+)?def\s+(test[\w_]*|Test[\w_]*)\s*\(",
        RegexOptions.Compiled);

    // Marker files we accept as "this is a Python project".
    private static readonly IReadOnlyList<string> MarkerFiles = new[]
    {
        "pyproject.toml", "setup.py", "setup.cfg", "requirements.txt", "Pipfile", "poetry.lock",
    };

    public Task<bool> DetectAsync(string workspace, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            return Task.FromResult(false);
        }
        try
        {
            foreach (var marker in MarkerFiles)
            {
                if (File.Exists(Path.Combine(workspace, marker))) return Task.FromResult(true);
                if (Directory.EnumerateFiles(workspace, marker, SearchOption.AllDirectories).Any())
                {
                    return Task.FromResult(true);
                }
            }
            // No marker file — fall back to "any .py file anywhere"
            // so a bare script workspace is still recognised.
            return Task.FromResult(Directory.EnumerateFiles(workspace, "*.py", SearchOption.AllDirectories).Any());
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public TestEntryPoint? IsTestReference(string filePath, IReadOnlyList<string> sourceLines, int line, int character)
    {
        if (line < 0 || line >= sourceLines.Count) return null;

        // Walk up to the enclosing `def`. Python uses indentation
        // for scope; we accept any `def` at any indent level since
        // tests can live inside test classes (`class TestX: def
        // test_y(self): ...`). For the pytest node id we'd ideally
        // capture the class context too — left for a follow-up.
        var start = Math.Min(line, sourceLines.Count - 1);
        var stop = Math.Max(0, start - 500);
        for (var i = start; i >= stop; i--)
        {
            var m = TestDefRegex.Match(sourceLines[i]);
            if (m.Success)
            {
                return new TestEntryPoint(
                    FilePath: filePath,
                    OwningPath: filePath,
                    TestName: m.Groups[1].Value,
                    LanguageId: this.LanguageId);
            }
            // Stop at a non-test `def` so we don't bubble out of the
            // enclosing helper into a sibling test.
            var trimmed = sourceLines[i].TrimStart();
            if (trimmed.StartsWith("def ", StringComparison.Ordinal)
                || trimmed.StartsWith("async def ", StringComparison.Ordinal))
            {
                return null;
            }
        }
        return null;
    }

    public string RewriteCommand(string originalCommand, string workspace, IReadOnlyList<TestEntryPoint> impacted)
    {
        // pytest node id format: `path/to/file.py::test_name`.
        // Operator's original command (e.g. `pytest`, `python -m
        // pytest --maxfail=1`) is preserved + node ids appended at
        // the end.
        var sb = new StringBuilder();
        sb.Append(originalCommand.TrimEnd());

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in impacted)
        {
            var rel = TryRelative(workspace, t.FilePath);
            var nodeId = $"{rel}::{t.TestName}";
            if (seen.Add(nodeId))
            {
                sb.Append(' ');
                sb.Append(nodeId);
            }
        }
        return sb.ToString();
    }

    private static string TryRelative(string workspace, string filePath)
    {
        try
        {
            return Path.GetRelativePath(workspace, filePath).Replace(Path.DirectorySeparatorChar, '/');
        }
        catch
        {
            return filePath.Replace(Path.DirectorySeparatorChar, '/');
        }
    }
}
