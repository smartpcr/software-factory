// -----------------------------------------------------------------------
// <copyright file="CiWorkflowYamlTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests;

using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.RepresentationModel;

/// <summary>
/// Meta-tests that parse the CI workflow YAML and assert required structure.
/// Defends against accidental deletion of required CI steps.
/// </summary>
[Trait("Category", "CiMeta")]
public class CiWorkflowYamlTests
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

    private static string WorkflowPath => Path.Combine(RepoRoot, ".github", "workflows", "ci-workflow-ui.yml");

    private static string LoadWorkflowYaml()
    {
        Assert.True(File.Exists(WorkflowPath), $"CI workflow not found at {WorkflowPath}");
        return File.ReadAllText(WorkflowPath);
    }

    private static YamlMappingNode ParseWorkflow()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(LoadWorkflowYaml()));
        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }

    [Fact]
    public void Yaml_IsValid_UTF8()
    {
        var text = LoadWorkflowYaml();
        Assert.False(string.IsNullOrWhiteSpace(text));
        // Parse should not throw
        ParseWorkflow();
    }

    [Fact]
    public void Jobs_Count_AtLeast4()
    {
        var root = ParseWorkflow();
        var jobs = (YamlMappingNode)root["jobs"];
        Assert.True(jobs.Children.Count >= 4, $"Expected at least 4 jobs, found {jobs.Children.Count}");
    }

    [Fact]
    public void TestDotnet_HasExplicitEnumParityStep()
    {
        var text = LoadWorkflowYaml();
        Assert.Contains("Category=EnumParity", text);
    }

    [Fact]
    public void TestDotnet_HasExplicitDtoParityStep()
    {
        var text = LoadWorkflowYaml();
        Assert.Contains("Category=DtoParity", text);
    }

    [Fact]
    public void TestDotnet_HasExplicitYamlRoundTripStep()
    {
        var text = LoadWorkflowYaml();
        Assert.Contains("Category=YamlRoundTrip", text);
    }

    [Fact]
    public void SetupDotnet_ReadsGlobalJson()
    {
        var text = LoadWorkflowYaml();
        Assert.Contains("global-json-file: global.json", text);
    }

    [Fact]
    public void SetupNode_CachesNpm()
    {
        var text = LoadWorkflowYaml();
        Assert.Contains("cache: npm", text);
    }

    [Fact]
    public void Build_UsesWarnAsError()
    {
        var text = LoadWorkflowYaml();
        Assert.Contains("-warnaserror", text);
    }

    [Fact]
    public void ActionPins_NoMasterOrMainRefs()
    {
        var text = LoadWorkflowYaml();
        var matches = Regex.Matches(text, @"uses:\s*\S+@(main|master)\b");
        Assert.Empty(matches);
    }

    [Fact]
    public void RunsOn_WindowsLatest()
    {
        var text = LoadWorkflowYaml();
        Assert.Contains("windows-latest", text);
    }

    [Fact]
    public void CoverageGate_ExcludesGeneratedFiles()
    {
        var text = LoadWorkflowYaml();
        Assert.Contains("engine-contract.generated.ts", text);
    }

    [Fact]
    public void CoverageGate_DownloadsArtifacts()
    {
        var text = LoadWorkflowYaml();
        Assert.Contains("download-artifact", text);
    }

    [Fact]
    public void CoverageGate_VerifiesClientLcov()
    {
        var text = LoadWorkflowYaml();
        Assert.Contains("lcov.info", text);
    }

    [Fact]
    public void Parses_And_ContainsAllRequiredSteps()
    {
        var text = LoadWorkflowYaml();
        var requiredStepPatterns = new[]
        {
            "Enum parity gate",
            "DTO parity gate",
            "YAML round-trip gate",
            "Run contract tests",
            "Upload test results",
            "Upload coverage",
            "Enforce client coverage threshold",
            "Enforce .NET coverage threshold",
        };
        foreach (var pattern in requiredStepPatterns)
        {
            Assert.Contains(pattern, text);
        }
    }

    [Fact]
    public void ConcurrencyGroup_CancelInProgress()
    {
        var text = LoadWorkflowYaml();
        Assert.Contains("cancel-in-progress: true", text);
    }

    [Fact]
    public void CoverageGate_EnforcesThreshold()
    {
        var text = LoadWorkflowYaml();
        // The coverage gate must check percentage and fail if below 100
        Assert.Contains("100", text);
        Assert.Contains("exit 1", text);
    }

    [Fact]
    public void BranchProtectionDoc_MatchesActualJobNames()
    {
        var docPath = Path.Combine(RepoRoot, "docs", "workflow-ui", "branch-protection.md");
        Assert.True(File.Exists(docPath));
        var text = File.ReadAllText(docPath);

        // The documented names must match the actual CI workflow job `name:` values
        var root = ParseWorkflow();
        var jobs = (YamlMappingNode)root["jobs"];
        foreach (var job in jobs.Children)
        {
            var jobNode = (YamlMappingNode)job.Value;
            if (jobNode.Children.ContainsKey("name"))
            {
                var jobName = ((YamlScalarNode)jobNode["name"]).Value!;
                Assert.Contains(jobName, text);
            }
        }
    }
}
