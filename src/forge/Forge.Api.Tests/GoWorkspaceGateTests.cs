// -----------------------------------------------------------------------
// <copyright file="GoWorkspaceGateTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Forge.Api.Tests;

using System.Collections.Generic;
using System.IO;
using Forge.Api.Services.Stories;
using Xunit;

/// <summary>
/// Pure-logic lock-in for <see cref="GoWorkspaceGate"/> — the go.work
/// multi-module awareness that lets the gate run `go test ./...` inside
/// each workspace module instead of finding zero packages from the root
/// (the root cause that drove an agent to commit a fake
/// `tools/forge_gate_proxy` shim).
/// </summary>
public sealed class GoWorkspaceGateTests
{
    [Theory]
    [InlineData("go test ./...", true)]
    [InlineData("  go   build ./...", true)]
    [InlineData("GO test ./...", true)]
    [InlineData("dotnet test", false)]
    [InlineData("cargo test", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsGoCommand_detects_go_program(string? command, bool expected)
    {
        Assert.Equal(expected, GoWorkspaceGate.IsGoCommand(command));
    }

    [Fact]
    public void ParseUseDirectives_handles_block_form_with_comments()
    {
        var text =
            "// header comment\n" +
            "go 1.25.1\n" +
            "\n" +
            "use (\n" +
            "\t.                          // root module\n" +
            "\t./services/agent-memory\n" +
            "\t./services/clean-code\n" +
            ")\n";

        var dirs = GoWorkspaceGate.ParseUseDirectives(text);

        Assert.Equal(new[] { ".", "services/agent-memory", "services/clean-code" }, dirs);
    }

    [Fact]
    public void ParseUseDirectives_handles_single_line_form()
    {
        var dirs = GoWorkspaceGate.ParseUseDirectives("go 1.22\nuse ./services/clean-code\n");
        Assert.Equal(new[] { "services/clean-code" }, dirs);
    }

    [Fact]
    public void ParseUseDirectives_dedupes_and_canonicalises_root()
    {
        var dirs = GoWorkspaceGate.ParseUseDirectives("use (\n  ./\n  .\n)\n");
        Assert.Equal(new[] { "." }, dirs);
    }

    [Fact]
    public void SelectImpactedModules_attributes_file_to_longest_prefix()
    {
        var modules = new[] { ".", "services/agent-memory", "services/clean-code" };
        var changed = new[]
        {
            "services/clean-code/internal/cli/walk/walker.go",
            "README.md", // root-owned
        };

        var impacted = GoWorkspaceGate.SelectImpactedModules(modules, changed);

        // clean-code (specific) + "." (owns the root README) — NOT agent-memory.
        Assert.Contains("services/clean-code", impacted);
        Assert.Contains(".", impacted);
        Assert.DoesNotContain("services/agent-memory", impacted);
    }

    [Fact]
    public void SelectImpactedModules_scopes_to_only_the_touched_service()
    {
        var modules = new[] { ".", "services/agent-memory", "services/clean-code" };
        var changed = new[] { "services/clean-code/policy/rulepacks/decoupling/cycles.yaml" };

        var impacted = GoWorkspaceGate.SelectImpactedModules(modules, changed);

        // The whole point: a clean-code-only diff must NOT drag in
        // agent-memory's (pre-existing-broken) tests.
        Assert.Equal(new[] { "services/clean-code" }, impacted);
    }

    [Fact]
    public void SelectImpactedModules_empty_changes_returns_all()
    {
        var modules = new[] { ".", "services/clean-code" };
        Assert.Equal(modules, GoWorkspaceGate.SelectImpactedModules(modules, System.Array.Empty<string>()));
        Assert.Equal(modules, GoWorkspaceGate.SelectImpactedModules(modules, null));
    }

    [Theory]
    [InlineData("go test ./...", "go test ./...")]
    [InlineData("go test -count=1 ./services/clean-code/internal/cli/walk/... -run '^TestX$'",
                "go test -count=1 -run '^TestX$' ./...")]
    [InlineData("go test -count=1 services/clean-code/...", "go test -count=1 ./...")]
    [InlineData("go build ./...", "go build ./...")]
    [InlineData("go test all", "go test ./...")]
    public void ScopeCommandToModule_strips_package_spec_keeps_flags(string input, string expected)
    {
        Assert.Equal(expected, GoWorkspaceGate.ScopeCommandToModule(input));
    }

    [Fact]
    public void ScopeCommandToModule_passes_through_non_go_command()
    {
        Assert.Equal("dotnet test", GoWorkspaceGate.ScopeCommandToModule("dotnet test"));
    }

    [Fact]
    public void TryReadWorkspaceModules_null_without_go_work()
    {
        var dir = Directory.CreateTempSubdirectory("forge-gowork-none-");
        try
        {
            Assert.Null(GoWorkspaceGate.TryReadWorkspaceModules(dir.FullName));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void TryReadWorkspaceModules_returns_only_real_modules()
    {
        var root = Directory.CreateTempSubdirectory("forge-gowork-");
        try
        {
            // go.work references three modules but only two exist with a go.mod.
            File.WriteAllText(Path.Combine(root.FullName, "go.work"),
                "go 1.25.1\nuse (\n  .\n  ./services/clean-code\n  ./services/ghost\n)\n");
            File.WriteAllText(Path.Combine(root.FullName, "go.mod"), "module example\n\ngo 1.22\n");
            var svc = Directory.CreateDirectory(Path.Combine(root.FullName, "services", "clean-code"));
            File.WriteAllText(Path.Combine(svc.FullName, "go.mod"), "module example/clean-code\n\ngo 1.25.1\n");
            // services/ghost has no directory/go.mod → must be dropped.

            var modules = GoWorkspaceGate.TryReadWorkspaceModules(root.FullName);

            Assert.NotNull(modules);
            Assert.Equal(new[] { ".", "services/clean-code" }, modules!);
        }
        finally { root.Delete(recursive: true); }
    }
}
