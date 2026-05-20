// -----------------------------------------------------------------------
// <copyright file="Phase59S2LanguageDetectorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using Forge.Api.Services.Stories.ImpactedTests.Lsp;
using Forge.Api.Services.Stories.ImpactedTests.Lsp.Languages;
using Xunit;

/// <summary>
/// Phase 59 Stage S2 — per-language <c>IsTestReference</c> predicate
/// + workspace-detect probe + command rewrite. These don't need a
/// real LSP server on PATH — the predicate is pure syntax over the
/// supplied source lines, the detect is pure file probing, and the
/// rewrite is pure string transform. Live integration tests against
/// rust-analyzer / gopls / pylsp / typescript-language-server are a
/// separate harness (skip-on-missing-PATH) tracked as a follow-up
/// once those servers are reliably installed on the CI host.
/// </summary>
public class Phase59S2LanguageDetectorTests
{
    private static int LineOf(string[] src, string needle)
    {
        for (var i = 0; i < src.Length; i++)
        {
            if (src[i].Contains(needle, StringComparison.Ordinal)) return i;
        }
        throw new Xunit.Sdk.XunitException($"line containing '{needle}' not found in fixture");
    }

    // ── Rust ─────────────────────────────────────────────────────────────

    [Fact]
    public void Rust_DetectsCargoToml()
    {
        var root = Path.Combine(Path.GetTempPath(), "phase59s2-rust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Cargo.toml"), "[package]\nname = \"x\"\nversion = \"0.1.0\"");

        var rust = new RustLanguageSupport();
        Assert.True(rust.DetectAsync(root, CancellationToken.None).Result);
    }

    [Fact]
    public void Rust_IsTestReference_MatchesStdTestAttribute()
    {
        var src = new[]
        {
            "use crate::math::add;",
            "",
            "#[test]",
            "fn it_adds_two_numbers() {",
            "    assert_eq!(add(2, 3), 5); // <-- the reference",
            "}",
        };
        var rust = new RustLanguageSupport();
        var refLine = LineOf(src, "<-- the reference");
        var tp = rust.IsTestReference("src/math_test.rs", src, refLine, 0);

        Assert.NotNull(tp);
        Assert.Equal("rust", tp!.LanguageId);
        Assert.Equal("it_adds_two_numbers", tp.TestName);
    }

    [Theory]
    [InlineData("#[tokio::test]")]
    [InlineData("#[async_std::test]")]
    [InlineData("#[actix_web::test]")]
    [InlineData("#[test]")]
    public void Rust_IsTestReference_RecognisesAsyncRuntimeVariants(string attribute)
    {
        var src = new[]
        {
            attribute,
            "async fn it_runs_async() {",
            "    actual_call();",
            "}",
        };
        var rust = new RustLanguageSupport();
        var tp = rust.IsTestReference("foo_test.rs", src, line: 2, character: 0);
        Assert.NotNull(tp);
        Assert.Equal("it_runs_async", tp!.TestName);
    }

    [Fact]
    public void Rust_IsTestReference_RejectsNonTestFn()
    {
        var src = new[]
        {
            "// A plain helper, not a test.",
            "pub fn helper() -> i32 {",
            "    actual_call()",
            "}",
        };
        var rust = new RustLanguageSupport();
        var tp = rust.IsTestReference("foo.rs", src, line: 2, character: 0);
        Assert.Null(tp);
    }

    [Fact]
    public void Rust_RewriteCommand_ListsMatchedFnNames()
    {
        var rust = new RustLanguageSupport();
        var impacted = new[]
        {
            new TestEntryPoint("a.rs", "a.rs", "test_alpha", "rust"),
            new TestEntryPoint("b.rs", "b.rs", "test_beta", "rust"),
            // Dedupe — same fn name twice shouldn't double-list.
            new TestEntryPoint("c.rs", "c.rs", "test_alpha", "rust"),
        };
        var cmd = rust.RewriteCommand("cargo test", "/work", impacted);
        Assert.Equal("cargo test -- test_alpha test_beta", cmd);
    }

    // ── Go ───────────────────────────────────────────────────────────────

    [Fact]
    public void Go_DetectsGoMod()
    {
        var root = Path.Combine(Path.GetTempPath(), "phase59s2-go-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "go.mod"), "module example.com/x\ngo 1.21");
        var go = new GoLanguageSupport();
        Assert.True(go.DetectAsync(root, CancellationToken.None).Result);
    }

    [Fact]
    public void Go_IsTestReference_MatchesTestXxxInTestFile()
    {
        var src = new[]
        {
            "package math",
            "",
            "import \"testing\"",
            "",
            "func TestAdd(t *testing.T) {",
            "    if Add(2, 3) != 5 { t.Fail() } // <-- ref",
            "}",
        };
        var go = new GoLanguageSupport();
        var refLine = LineOf(src, "<-- ref");
        var tp = go.IsTestReference("/work/math/math_test.go", src, refLine, 0);
        Assert.NotNull(tp);
        Assert.Equal("TestAdd", tp!.TestName);
        Assert.EndsWith("math", tp.OwningPath);
    }

    [Fact]
    public void Go_IsTestReference_RejectsNonTestFile()
    {
        // Same fn shape but in a non-_test.go file → must not match.
        var src = new[]
        {
            "func TestAdd(t *testing.T) {",
            "    actual_call() // <-- ref",
            "}",
        };
        var go = new GoLanguageSupport();
        var tp = go.IsTestReference("/work/math.go", src, line: 1, character: 0);
        Assert.Null(tp);
    }

    [Fact]
    public void Go_RewriteCommand_AnchorsTestNamesInRunRegex()
    {
        var go = new GoLanguageSupport();
        var impacted = new[]
        {
            new TestEntryPoint("/w/foo/foo_test.go", "/w/foo", "TestAlpha", "go"),
            new TestEntryPoint("/w/foo/foo_test.go", "/w/foo", "TestBeta", "go"),
        };
        var cmd = go.RewriteCommand("go test ./...", "/w", impacted);
        // Single package → narrow to that package.
        Assert.Contains("./foo/...", cmd);
        Assert.Contains("-run '^TestAlpha$|^TestBeta$'", cmd);
    }

    [Fact]
    public void Go_RewriteCommand_WidensToAllPackagesAcrossPackages()
    {
        var go = new GoLanguageSupport();
        var impacted = new[]
        {
            new TestEntryPoint("/w/foo/foo_test.go", "/w/foo", "TestAlpha", "go"),
            new TestEntryPoint("/w/bar/bar_test.go", "/w/bar", "TestBeta", "go"),
        };
        var cmd = go.RewriteCommand("go test ./...", "/w", impacted);
        Assert.Contains("./...", cmd);
        Assert.Contains("-run '^TestAlpha$|^TestBeta$'", cmd);
    }

    // ── Python ───────────────────────────────────────────────────────────

    [Fact]
    public void Python_DetectsPyprojectToml()
    {
        var root = Path.Combine(Path.GetTempPath(), "phase59s2-py-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "pyproject.toml"), "[project]\nname=\"x\"");
        var py = new PythonLanguageSupport();
        Assert.True(py.DetectAsync(root, CancellationToken.None).Result);
    }

    [Fact]
    public void Python_IsTestReference_MatchesTestUnderscoreDef()
    {
        var src = new[]
        {
            "from math import add",
            "",
            "def test_adds_two_numbers():",
            "    assert add(2, 3) == 5  # <-- ref",
        };
        var py = new PythonLanguageSupport();
        var refLine = LineOf(src, "<-- ref");
        var tp = py.IsTestReference("/work/test_math.py", src, refLine, 0);
        Assert.NotNull(tp);
        Assert.Equal("test_adds_two_numbers", tp!.TestName);
    }

    [Fact]
    public void Python_IsTestReference_RejectsNonTestDef()
    {
        var src = new[]
        {
            "def helper():",
            "    return 42  # <-- ref",
        };
        var py = new PythonLanguageSupport();
        var tp = py.IsTestReference("/work/test_math.py", src, line: 1, character: 0);
        Assert.Null(tp);
    }

    [Fact]
    public void Python_RewriteCommand_AppendsPytestNodeIds()
    {
        var py = new PythonLanguageSupport();
        var impacted = new[]
        {
            new TestEntryPoint("/w/tests/test_a.py", "/w/tests/test_a.py", "test_alpha", "python"),
            new TestEntryPoint("/w/tests/test_b.py", "/w/tests/test_b.py", "test_beta", "python"),
        };
        var cmd = py.RewriteCommand("pytest", "/w", impacted);
        Assert.Equal("pytest tests/test_a.py::test_alpha tests/test_b.py::test_beta", cmd);
    }

    // ── TypeScript ───────────────────────────────────────────────────────

    [Fact]
    public void TypeScript_DetectsTsconfigJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "phase59s2-ts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "tsconfig.json"), "{\"compilerOptions\":{}}");
        var ts = new TypeScriptLanguageSupport();
        Assert.True(ts.DetectAsync(root, CancellationToken.None).Result);
    }

    [Fact]
    public void TypeScript_IsTestReference_MatchesItBlock()
    {
        var src = new[]
        {
            "import { add } from './math';",
            "import { describe, it, expect } from 'vitest';",
            "",
            "describe('add', () => {",
            "  it('adds two numbers', () => {",
            "    expect(add(2, 3)).toBe(5); // <-- ref",
            "  });",
            "});",
        };
        var ts = new TypeScriptLanguageSupport();
        var refLine = LineOf(src, "<-- ref");
        var tp = ts.IsTestReference("/work/math.test.ts", src, refLine, 0);
        Assert.NotNull(tp);
        Assert.Equal("adds two numbers", tp!.TestName);
    }

    [Fact]
    public void TypeScript_IsTestReference_MatchesTestCall()
    {
        // `test(...)` is jest's equivalent of `it(...)`.
        var src = new[]
        {
            "test('alpha case', () => {",
            "  doSomething(); // <-- ref",
            "});",
        };
        var ts = new TypeScriptLanguageSupport();
        var tp = ts.IsTestReference("/w/foo.spec.ts", src, line: 1, character: 0);
        Assert.NotNull(tp);
        Assert.Equal("alpha case", tp!.TestName);
    }

    [Fact]
    public void TypeScript_IsTestReference_RejectsNonTestCallsite()
    {
        var src = new[]
        {
            "function helper() {",
            "  doSomething(); // <-- ref",
            "}",
        };
        var ts = new TypeScriptLanguageSupport();
        var tp = ts.IsTestReference("/w/util.ts", src, line: 1, character: 0);
        Assert.Null(tp);
    }

    [Fact]
    public void TypeScript_RewriteCommand_AppendsDashTRegex()
    {
        var ts = new TypeScriptLanguageSupport();
        var impacted = new[]
        {
            new TestEntryPoint("/w/a.test.ts", "/w/a.test.ts", "adds two numbers", "typescript"),
            new TestEntryPoint("/w/b.test.ts", "/w/b.test.ts", "handles negatives", "typescript"),
        };
        var cmd = ts.RewriteCommand("npx jest", "/w", impacted);
        // Names are regex-escaped + OR'd.
        Assert.Contains("-t 'adds two numbers|handles negatives'", cmd);
    }

    [Fact]
    public void TypeScript_RewriteCommand_DropsExistingDashT()
    {
        var ts = new TypeScriptLanguageSupport();
        var impacted = new[]
        {
            new TestEntryPoint("/w/a.test.ts", "/w/a.test.ts", "x", "typescript"),
        };
        var cmd = ts.RewriteCommand("npx jest -t 'old filter'", "/w", impacted);
        Assert.DoesNotContain("old filter", cmd);
        Assert.Contains("-t 'x'", cmd);
    }

    [Fact]
    public void TypeScript_RewriteCommand_EscapesRegexMetacharsInTestNames()
    {
        var ts = new TypeScriptLanguageSupport();
        var impacted = new[]
        {
            new TestEntryPoint("/w/a.test.ts", "/w/a.test.ts", "handles (a|b) cases", "typescript"),
        };
        var cmd = ts.RewriteCommand("npx jest", "/w", impacted);
        // Parens + pipe should be backslash-escaped so jest's RegExp
        // ctor doesn't choke.
        Assert.Contains("-t 'handles \\(a\\|b\\) cases'", cmd);
    }
}
