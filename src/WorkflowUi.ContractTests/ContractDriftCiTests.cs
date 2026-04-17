// -----------------------------------------------------------------------
// <copyright file="ContractDriftCiTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests;

using System.IO;
using System.Text;
using GenContract.Emitters;
using Xunit;

/// <summary>
/// Meta-tests validating CI contract-drift job and generator parity.
/// </summary>
[Trait("Category", "ContractDrift")]
public class ContractDriftCiTests
{
    private readonly string workflowYaml;

    public ContractDriftCiTests()
    {
        // Navigate from the test output directory to the repo root
        // Output is at: src/WorkflowUi.ContractTests/bin/Debug/net8.0/win-x64/
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Workflow.sln")))
        {
            dir = dir.Parent;
        }

        var repoRoot = dir?.FullName ?? throw new InvalidOperationException("Could not find repo root");
        this.workflowYaml = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "ci-workflow-ui.yml"));
    }

    [Fact]
    public void Job_Exists()
    {
        Assert.Contains("contract-drift:", this.workflowYaml);
    }

    [Fact]
    public void Job_Needs_BuildDotnet()
    {
        Assert.Contains("needs: build-dotnet", this.workflowYaml);
    }

    [Fact]
    public void Job_UsesWindowsLatest()
    {
        // The contract-drift section should contain windows-latest
        var idx = this.workflowYaml.IndexOf("contract-drift:", StringComparison.Ordinal);
        Assert.True(idx >= 0, "contract-drift job not found");
        var afterJob = this.workflowYaml[idx..];
        Assert.Contains("windows-latest", afterJob);
    }

    [Fact]
    public void Step_ExplicitlyExits1_OnDiff()
    {
        Assert.Contains("exit 1", this.workflowYaml);
    }

    [Fact]
    public void Step_NamesGeneratorCommand()
    {
        Assert.Contains("dotnet run --project tools/GenContract", this.workflowYaml);
    }

    [Fact]
    public void Diff_UsesComparison()
    {
        Assert.Contains("Contract drift detected", this.workflowYaml);
    }

    [Fact]
    public void CommittedGenerated_MatchesGenerator()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Workflow.sln")))
        {
            dir = dir.Parent;
        }

        var repoRoot = dir?.FullName ?? throw new InvalidOperationException("Could not find repo root");
        var committedPath = Path.Combine(repoRoot, "src", "WorkflowUI", "src", "engine-contract.generated.ts");
        var committedBytes = File.ReadAllBytes(committedPath);

        // Generate in-process
        var w = new TsWriter();
        var engineVersion = typeof(ExecutionEngine.Workflow.WorkflowDefinition)
            .Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        w.Line("// GENERATED. Do not edit. Run tools/GenContract to regenerate.");
        w.Line($"// Engine assembly version: {engineVersion}");
        w.Line();
        var enumTypes = EnumEmitter.GetEnumTypes();
        EnumEmitter.Emit(w, enumTypes);
        PolicyEmitter.Emit(w);
        DtoEmitter.Emit(w);
        ConnectionEmitter.Emit(w);

        // Add WorkflowDefinitionDto (same as Program.cs)
        w.Line("export interface WorkflowDefinitionDto {");
        w.Indent();
        w.Line("workflowId?: string;");
        w.Line("workflowName?: string;");
        w.Line("description?: string;");
        w.Line("version?: string;");
        w.Line("nodes?: NodeDto[];");
        w.Line("connections?: NodeConnectionDto[];");
        w.Line("metadata?: Record<string, string>;");
        w.Line("entryPointNodeId?: string;");
        w.Line("maxConcurrency?: number;");
        w.Line("allowPause?: boolean;");
        w.Line("timeoutSeconds?: number;");
        w.Line("defaultVariables?: Record<string, string>;");
        w.Dedent();
        w.Line("}");
        w.Line();

        var generatedBytes = Encoding.UTF8.GetBytes(w.ToString());
        Assert.Equal(committedBytes, generatedBytes);
    }

    [Fact]
    public void GeneratorReadmePresent_AndMentionsCommand()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Workflow.sln")))
        {
            dir = dir.Parent;
        }

        var repoRoot = dir?.FullName ?? throw new InvalidOperationException("Could not find repo root");
        var readmePath = Path.Combine(repoRoot, "tools", "GenContract", "README.md");
        Assert.True(File.Exists(readmePath), "GenContract README.md must exist");
        var content = File.ReadAllText(readmePath);
        Assert.Contains("dotnet run --project tools/GenContract", content);
    }

    private string ExtractJobSection(string jobName)
    {
        var idx = this.workflowYaml.IndexOf($"{jobName}:", StringComparison.Ordinal);
        if (idx < 0)
        {
            return string.Empty;
        }

        var nextJobIdx = this.workflowYaml.IndexOf("\n  ", idx + jobName.Length + 10, StringComparison.Ordinal);
        return nextJobIdx > idx
            ? this.workflowYaml[idx..nextJobIdx]
            : this.workflowYaml[idx..];
    }
}
