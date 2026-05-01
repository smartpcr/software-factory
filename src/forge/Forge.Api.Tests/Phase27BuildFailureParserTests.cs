// -----------------------------------------------------------------------
// <copyright file="Phase27BuildFailureParserTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using FluentAssertions;
using Forge.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// forge_implementation.md Phase 27 Q27.6 — the regex pass produces
/// structured FailurePoint rows from a per-stage log tail. These tests
/// drive Pass 1 directly (no DbContext) — see Phase27BuildSyncTests for
/// the end-to-end persistence path.
/// </summary>
public class Phase27BuildFailureParserTests
{
    private static BuildFailureParser NewParser() =>
        new(new ForgeMetrics(), NullLogger<BuildFailureParser>.Instance);

    [Fact]
    public void DotNet_compile_error_pattern_extracts_file_and_line()
    {
        var parser = NewParser();
        var log = "MSBUILD : error CS1234: identifier not found [path/to/file.cs:42]";

        var pts = parser.RunPass1(log, sourceOffsetBase: 0);

        pts.Should().HaveCount(1);
        pts[0].Kind.Should().Be("compile");
        pts[0].FilePath.Should().Be("path/to/file.cs");
        pts[0].Line.Should().Be(42);
        pts[0].Message.Should().Contain("identifier not found");
    }

    [Fact]
    public void Cmake_FAILED_pattern_extracts_target()
    {
        var parser = NewParser();
        var log = "FAILED: src/foo.o";
        var pts = parser.RunPass1(log, sourceOffsetBase: 0);
        pts.Should().Contain(p => p.Kind == "build" && p.FilePath == "src/foo.o");
    }

    [Fact]
    public void Test_assert_pattern_extracts_file_and_line()
    {
        var parser = NewParser();
        var log = "assert.fail at test/foo.spec.ts:88";
        var pts = parser.RunPass1(log, sourceOffsetBase: 0);
        pts.Should().Contain(p => p.Kind == "test" && p.FilePath == "test/foo.spec.ts" && p.Line == 88);
    }

    [Fact]
    public void Npm_pattern_extracts_code()
    {
        var parser = NewParser();
        var log = "npm ERR! code ELIFECYCLE";
        var pts = parser.RunPass1(log, sourceOffsetBase: 0);
        pts.Should().Contain(p => p.Kind == "npm" && p.Message == "ELIFECYCLE");
    }

    [Fact]
    public void Generic_stack_trace_pattern_extracts_runtime_failure()
    {
        var parser = NewParser();
        var log = "Error: something broke at /path/file.cs:42";
        var pts = parser.RunPass1(log, sourceOffsetBase: 0);
        pts.Should().Contain(p => p.Kind == "runtime" && p.FilePath == "/path/file.cs" && p.Line == 42);
    }

    [Fact]
    public void Multi_failure_log_yields_multiple_rows()
    {
        var parser = NewParser();
        var log = "error CS1234: foo [a.cs:1]\n" +
                  "error CS5678: bar [b.cs:2]\n" +
                  "FAILED: src/quux.o\n";
        var pts = parser.RunPass1(log, sourceOffsetBase: 0);
        pts.Count.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void Empty_log_returns_empty_list()
    {
        var parser = NewParser();
        parser.RunPass1(string.Empty, 0).Should().BeEmpty();
        parser.RunPass1("nothing useful here", 0).Should().BeEmpty();
    }

    [Fact]
    public void Source_offset_base_negative_marks_artifact_match()
    {
        var parser = NewParser();
        var log = "error CS9999: from artifact [x.cs:5]";
        var pts = parser.RunPass1(log, sourceOffsetBase: -1);
        pts.Should().HaveCount(1);
        pts[0].SourceLogTailOffset.Should().Be(-1);
    }
}
