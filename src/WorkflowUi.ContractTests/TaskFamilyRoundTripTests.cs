// -----------------------------------------------------------------------
// <copyright file="TaskFamilyRoundTripTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests;

using System;
using System.IO;
using System.Linq;
using ExecutionEngine.Nodes.Definitions;
using ExecutionEngine.Workflow;
using Xunit;

/// <summary>
/// Round-trip tests for task-family node kinds.
/// </summary>
[Trait("Category", "TaskFamilyRoundTrip")]
public class TaskFamilyRoundTripTests
{
    private readonly WorkflowSerializer serializer = new();

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

    private static string FixturesDir => Path.Combine(RepoRoot, "src", "WorkflowUi.ContractTests", "Fixtures");

    [Fact]
    public void SetterMinimal_RoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "setter-minimal.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        Assert.True(wf.Nodes.All(n => n is SetterNodeDefinition));
        var reYaml = this.serializer.ToYaml(wf);
        var wf2 = this.serializer.FromYaml(reYaml);
        Assert.Equal(wf.Nodes.Count, wf2.Nodes.Count);
    }

    [Fact]
    public void CSharp_NullAssemblyPath_PreservedAfterRoundTrip()
    {
        var def = new CSharpNodeDefinition
        {
            NodeId = "cs1",
            AssemblyPath = null,
            TypeName = null,
        };
        var wf = Wrap(def);
        var yaml = this.serializer.ToYaml(wf);
        var back = this.serializer.FromYaml(yaml);
        var cs = back.Nodes.OfType<CSharpNodeDefinition>().First();
        Assert.Null(cs.AssemblyPath);
    }

    [Fact]
    public void CSharpScript_ScriptContent_PreservedAfterRoundTrip()
    {
        var def = new CSharpScriptNodeDefinition
        {
            NodeId = "css1",
            ScriptContent = "Console.WriteLine(\"hello\");",
        };
        var wf = Wrap(def);
        var yaml = this.serializer.ToYaml(wf);
        var back = this.serializer.FromYaml(yaml);
        var css = back.Nodes.OfType<CSharpScriptNodeDefinition>().First();
        Assert.Equal("Console.WriteLine(\"hello\");", css.ScriptContent);
    }

    [Fact]
    public void PowerShell_ScriptPath_PreservedAfterRoundTrip()
    {
        var def = new PowerShellScriptNodeDefinition
        {
            NodeId = "ps1",
            ScriptPath = "scripts/deploy.ps1",
        };
        var wf = Wrap(def);
        var yaml = this.serializer.ToYaml(wf);
        var back = this.serializer.FromYaml(yaml);
        var ps = back.Nodes.OfType<PowerShellScriptNodeDefinition>().First();
        Assert.Equal("scripts/deploy.ps1", ps.ScriptPath);
    }

    [Fact]
    public void CSharpMinimal_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "csharp-minimal.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        Assert.Single(wf.Nodes);
        var cs = wf.Nodes.OfType<CSharpNodeDefinition>().First();
        Assert.Equal("cs1", cs.NodeId);
    }

    [Fact]
    public void CSharpScriptInline_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "csharp-script-inline.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var css = wf.Nodes.OfType<CSharpScriptNodeDefinition>().First();
        Assert.Contains("Console.WriteLine", css.ScriptContent);
    }

    [Fact]
    public void CSharpScriptFile_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "csharp-script-file.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var css = wf.Nodes.OfType<CSharpScriptNodeDefinition>().First();
        Assert.Equal("scripts/task.csx", css.ScriptPath);
    }

    [Fact]
    public void CSharpScriptBoth_Fixture_LoadsButHasBothFieldsSet()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "csharp-script-both.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var css = wf.Nodes.OfType<CSharpScriptNodeDefinition>().First();
        Assert.False(string.IsNullOrEmpty(css.ScriptPath));
        Assert.False(string.IsNullOrEmpty(css.ScriptContent));
    }

    [Fact]
    public void PowerShellScriptWithModules_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "powershell-script-with-modules.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var ps = wf.Nodes.OfType<PowerShellScriptNodeDefinition>().First();
        Assert.Equal("scripts/deploy.ps1", ps.ScriptPath);
    }

    [Fact]
    public void PowerShellScriptEmptyPath_Fixture_LoadsWithEmptyScriptPath()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "powershell-script-empty-path.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var ps = wf.Nodes.OfType<PowerShellScriptNodeDefinition>().First();
        Assert.True(string.IsNullOrEmpty(ps.ScriptPath));
    }

    [Fact]
    public void CSharpTaskExecutor_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "csharp-task-executor.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var cst = wf.Nodes.OfType<CSharpTaskNodeDefinition>().First();
        Assert.Equal("MyApp.Tasks.MyExecutor", cst.ExecutorTypeName);
    }

    [Fact]
    public void CSharpTaskInline_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "csharp-task-inline.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var cst = wf.Nodes.OfType<CSharpTaskNodeDefinition>().First();
        Assert.Contains("Console.WriteLine", cst.ScriptContent);
    }

    [Fact]
    public void CSharpTaskEmpty_Fixture_LoadsAsDraft()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "csharp-task-empty.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var cst = wf.Nodes.OfType<CSharpTaskNodeDefinition>().First();
        Assert.Equal("cst1", cst.NodeId);
    }

    [Fact]
    public void PowerShellTaskInline_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "powershell-task-inline.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var pst = wf.Nodes.OfType<PowerShellTaskNodeDefinition>().First();
        Assert.Contains("Write-Host", pst.ScriptContent);
    }

    [Fact]
    public void PowerShellTaskFilepath_Fixture_LoadsAndRoundTrips()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "powershell-task-filepath.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var pst = wf.Nodes.OfType<PowerShellTaskNodeDefinition>().First();
        Assert.Equal("scripts/task.ps1", pst.ScriptPath);
    }

    [Fact]
    public void PowerShellTaskEngineQuirk_Fixture_LoadsWithEmptyScriptPath()
    {
        var yaml = File.ReadAllText(Path.Combine(FixturesDir, "powershell-task-engine-quirk.yaml"));
        var wf = this.serializer.FromYaml(yaml);
        var pst = wf.Nodes.OfType<PowerShellTaskNodeDefinition>().First();
        Assert.True(string.IsNullOrEmpty(pst.ScriptPath));
    }

    private static WorkflowDefinition Wrap(NodeDefinition node)
    {
        return new WorkflowDefinition
        {
            WorkflowId = "task-family-test",
            WorkflowName = "Task Family Test",
            Nodes = [node],
            Connections = [],
        };
    }
}
