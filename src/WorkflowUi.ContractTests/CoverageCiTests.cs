// -----------------------------------------------------------------------
// <copyright file="CoverageCiTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests;

using System.IO;
using Xunit;

/// <summary>
/// Meta-tests validating coverage CI job and codecov.yml configuration.
/// </summary>
[Trait("Category", "CoverageCi")]
public class CoverageCiTests
{
    private readonly string workflowYaml;
    private readonly string codecovYaml;
    private readonly string repoRoot;

    public CoverageCiTests()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Workflow.sln")))
        {
            dir = dir.Parent;
        }

        this.repoRoot = dir?.FullName ?? throw new InvalidOperationException("Could not find repo root");
        this.workflowYaml = File.ReadAllText(Path.Combine(this.repoRoot, ".github", "workflows", "ci-workflow-ui.yml"));
        this.codecovYaml = File.ReadAllText(Path.Combine(this.repoRoot, "codecov.yml"));
    }

    [Fact]
    public void CodecovYaml_Exists()
    {
        Assert.True(File.Exists(Path.Combine(this.repoRoot, "codecov.yml")));
    }

    [Fact]
    public void CodecovYaml_Parses()
    {
        Assert.False(string.IsNullOrWhiteSpace(this.codecovYaml));
        Assert.Contains("coverage:", this.codecovYaml);
    }

    [Fact]
    public void CodecovYaml_ProjectTarget_Is100()
    {
        Assert.Contains("target: 100%", this.codecovYaml);
    }

    [Fact]
    public void CodecovYaml_PatchTarget_Is100()
    {
        Assert.Contains("target: 100%", this.codecovYaml);
    }

    [Fact]
    public void CodecovYaml_IgnoresGeneratedContract()
    {
        Assert.Contains("src/WorkflowUI/src/engine-contract.generated.ts", this.codecovYaml);
    }

    [Fact]
    public void CodecovYaml_IgnoresGenContract()
    {
        Assert.Contains("tools/GenContract/**", this.codecovYaml);
    }

    [Fact]
    public void Workflow_HasCoveragePublishJob()
    {
        Assert.Contains("coverage-publish:", this.workflowYaml);
    }

    [Fact]
    public void Workflow_CoveragePublish_Needs_TestDotnetAndTestClient()
    {
        Assert.Contains("needs: [test-dotnet, test-client]", this.workflowYaml);
    }

    [Fact]
    public void Workflow_UsesReportGenerator()
    {
        Assert.Contains("reportgenerator", this.workflowYaml);
    }

    [Fact]
    public void Workflow_CodecovActionPinned_V4()
    {
        Assert.Contains("codecov/codecov-action@v4", this.workflowYaml);
    }
}
