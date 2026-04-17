// -----------------------------------------------------------------------
// <copyright file="CodeownersTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests;

using System;
using System.IO;
using System.Linq;
using Xunit;

/// <summary>
/// Tests that CODEOWNERS covers the required paths for the Workflow UI project.
/// </summary>
[Trait("Category", "CiMeta")]
public class CodeownersTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Workflow.sln")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            return dir ?? throw new InvalidOperationException("Cannot find repo root.");
        }
    }

    private static string CodeownersPath
    {
        get
        {
            var rootPath = Path.Combine(RepoRoot, "CODEOWNERS");
            if (File.Exists(rootPath))
            {
                return rootPath;
            }

            var ghPath = Path.Combine(RepoRoot, ".github", "CODEOWNERS");
            return ghPath;
        }
    }

    private static string[] LoadCodeownersLines()
    {
        Assert.True(File.Exists(CodeownersPath), $"CODEOWNERS not found at {CodeownersPath}");
        return File.ReadAllLines(CodeownersPath)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'))
            .ToArray();
    }

    [Fact]
    public void File_Exists()
    {
        Assert.True(File.Exists(CodeownersPath));
    }

    [Fact]
    public void Parses_WithoutError()
    {
        var lines = LoadCodeownersLines();
        Assert.NotEmpty(lines);
    }

    [Fact]
    public void CoversWorkflowUiPlan()
    {
        var lines = LoadCodeownersLines();
        Assert.Contains(lines, l => l.Contains("docs/workflow-ui-plan.md"));
    }

    [Fact]
    public void CoversWorkflowUiDocsFolder()
    {
        var lines = LoadCodeownersLines();
        Assert.Contains(lines, l => l.Contains("docs/workflow-ui/"));
    }

    [Fact]
    public void CoversCiWorkflowYaml()
    {
        var lines = LoadCodeownersLines();
        Assert.Contains(lines, l => l.Contains("ci-workflow-ui.yml"));
    }

    [Fact]
    public void CoversContractTestsProject()
    {
        var lines = LoadCodeownersLines();
        Assert.Contains(lines, l => l.Contains("src/WorkflowUi.ContractTests/"));
    }

    [Fact]
    public void NoDanglingHandle()
    {
        var lines = LoadCodeownersLines();
        foreach (var line in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // First part is the path pattern, rest are owners
            for (int i = 1; i < parts.Length; i++)
            {
                Assert.StartsWith("@", parts[i]);
            }
        }
    }

    [Fact]
    public void BranchProtectionDoc_MentionsAllRequiredChecks()
    {
        var docPath = Path.Combine(RepoRoot, "docs", "workflow-ui", "branch-protection.md");
        Assert.True(File.Exists(docPath), $"branch-protection.md not found at {docPath}");
        var text = File.ReadAllText(docPath);

        var requiredChecks = new[]
        {
            "Build (.NET)",
            "Test (.NET)",
            "Test (Client)",
            "Coverage gate",
        };

        foreach (var check in requiredChecks)
        {
            Assert.Contains(check, text);
        }
    }
}
