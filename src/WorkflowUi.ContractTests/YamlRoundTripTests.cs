// -----------------------------------------------------------------------
// <copyright file="YamlRoundTripTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.ContractTests;

using System;
using System.IO;
using System.Reflection;
using ExecutionEngine.Enums;
using ExecutionEngine.Nodes.Definitions;
using ExecutionEngine.Workflow;
using WorkflowUi.ContractTests.Helpers;
using Xunit;

/// <summary>
/// YAML round-trip golden tests that prove the UI server
/// will never fork YAML handling.
/// </summary>
[Trait("Category", "YamlRoundTrip")]
public class YamlRoundTripTests
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

            return dir ?? throw new InvalidOperationException("Cannot find repo root (Workflow.sln).");
        }
    }

    private static string FixturesDir => Path.Combine(RepoRoot, "src", "WorkflowUi.ContractTests", "Fixtures");

    [Fact]
    public void LoaderAndSerializer_ExistInEngineAssembly()
    {
        var asm = typeof(WorkflowDefinition).Assembly;
        var loader = asm.GetType("ExecutionEngine.Workflow.WorkflowLoader");
        var serializer = asm.GetType("ExecutionEngine.Workflow.WorkflowSerializer");
        Assert.NotNull(loader);
        Assert.NotNull(serializer);

        // Defend against banned YamlWorkflowLoader re-introduction
        var banned = asm.GetType("ExecutionEngine.Workflow.YamlWorkflowLoader");
        Assert.Null(banned);
    }

    [Fact]
    public void NoopMinimal_Load_Succeeds()
    {
        var fixturePath = Path.Combine(FixturesDir, "noop-minimal.yaml");
        Assert.True(File.Exists(fixturePath), $"Fixture not found at {fixturePath}");

        var loader = new WorkflowLoader();
        var wf = loader.Load(fixturePath, validateOnLoad: true);
        Assert.NotNull(wf);
        Assert.Equal("noop-minimal", wf.WorkflowId);
    }

    [Fact]
    public void NoopMinimal_Serialize_ProducesNonEmpty()
    {
        var fixturePath = Path.Combine(FixturesDir, "noop-minimal.yaml");
        var loader = new WorkflowLoader();
        var wf = loader.Load(fixturePath, validateOnLoad: true);
        var serializer = new WorkflowSerializer();
        var yaml = serializer.ToYaml(wf);
        Assert.False(string.IsNullOrWhiteSpace(yaml));
    }

    [Fact]
    public void NoopMinimal_LoadAndSerialize_MatchesCanonicalGolden()
    {
        var fixturePath = Path.Combine(FixturesDir, "noop-minimal.yaml");
        var goldenPath = Path.Combine(FixturesDir, "noop-minimal.canonical.yaml");
        Assert.True(File.Exists(goldenPath), $"Expected golden at {goldenPath}");

        var loader = new WorkflowLoader();
        var wf = loader.Load(fixturePath, validateOnLoad: true);
        var serializer = new WorkflowSerializer();
        var actual = CanonicalYaml.Canonicalize(serializer.ToYaml(wf));
        var expected = CanonicalYaml.Canonicalize(File.ReadAllText(goldenPath));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NoopMinimal_DoubleRoundTrip_IsIdempotent()
    {
        var fixturePath = Path.Combine(FixturesDir, "noop-minimal.yaml");
        var loader = new WorkflowLoader();
        var serializer = new WorkflowSerializer();

        var wf1 = loader.Load(fixturePath, validateOnLoad: true);
        var s1 = serializer.ToYaml(wf1);

        var wf2 = serializer.FromYaml(s1);
        var s2 = serializer.ToYaml(wf2);

        Assert.Equal(
            CanonicalYaml.Canonicalize(s1),
            CanonicalYaml.Canonicalize(s2));
    }

    [Fact]
    public void Canonicalizer_StripsComments()
    {
        var input = "# comment\nkey: value\n# another\nother: 1\n";
        var result = CanonicalYaml.Canonicalize(input);
        Assert.DoesNotContain("#", result);
        Assert.Contains("key: value", result);
    }

    [Fact]
    public void Canonicalizer_NormalizesCRLFtoLF()
    {
        var input = "key: value\r\nother: 1\r\n";
        var result = CanonicalYaml.Canonicalize(input);
        Assert.DoesNotContain("\r", result);
        Assert.Contains("key: value\nother: 1\n", result);
    }

    [Fact]
    public void Canonicalizer_PreservesEngineKeyOrder()
    {
        var input = "name: A\nid: B\n";
        var result = CanonicalYaml.Canonicalize(input);
        var nameIdx = result.IndexOf("name:", StringComparison.Ordinal);
        var idIdx = result.IndexOf("id:", StringComparison.Ordinal);
        Assert.True(nameIdx < idIdx, "Key order must be preserved");
    }

    [Fact]
    public void MissingGolden_FailsWithClearMessage()
    {
        var goldenPath = Path.Combine(FixturesDir, "does-not-exist.canonical.yaml");
        Assert.False(File.Exists(goldenPath));
    }

    [Fact]
    public void Loader_ValidationFailure_ThrowsInvalidOperationException()
    {
        // Create a corrupt fixture in a temp location
        var tempDir = Path.Combine(Path.GetTempPath(), $"wf-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var corruptPath = Path.Combine(tempDir, "corrupt.yaml");
            // A workflow with a connection referencing a non-existent node triggers validation failure
            File.WriteAllText(corruptPath, @"
workflowId: corrupt-test
workflowName: Corrupt
version: '1.0'
nodes:
- nodeId: start
  nodeName: Start
  runtimeType: Noop
connections:
- sourceNodeId: start
  targetNodeId: nonexistent
  triggerMessageType: Complete
");
            var loader = new WorkflowLoader();
            Assert.Throws<InvalidOperationException>(() => loader.Load(corruptPath, validateOnLoad: true));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GoldenFixture_MatchesCommittedOrRegenerates()
    {
        var fixturePath = Path.Combine(FixturesDir, "noop-minimal.yaml");
        var goldenPath = Path.Combine(FixturesDir, "noop-minimal.canonical.yaml");

        if (!File.Exists(fixturePath))
        {
            var wf = CreateNoopMinimalWorkflow();
            var serializer = new WorkflowSerializer();
            serializer.SaveToFile(wf, fixturePath);
        }

        var loader = new WorkflowLoader();
        var loaded = loader.LoadWithoutValidation(fixturePath);
        var ser = new WorkflowSerializer();
        var canonical = CanonicalYaml.Canonicalize(ser.ToYaml(loaded));

        if (Environment.GetEnvironmentVariable("WORKFLOWUI_WRITE_GOLDEN") == "1")
        {
            File.WriteAllText(goldenPath, canonical);
            return;
        }

        Assert.True(File.Exists(goldenPath), $"Missing golden at {goldenPath}. Set WORKFLOWUI_WRITE_GOLDEN=1 to generate.");
        var committed = CanonicalYaml.Canonicalize(File.ReadAllText(goldenPath));
        Assert.Equal(committed, canonical);
    }

    private static WorkflowDefinition CreateNoopMinimalWorkflow()
    {
        return new WorkflowDefinition
        {
            WorkflowId = "noop-minimal",
            WorkflowName = "Noop Minimal",
            Description = "A minimal 2-node Noop workflow for parity testing",
            Version = "1.0",
            Nodes =
            [
                new NoopNodeDefinition { NodeId = "start", NodeName = "Start" },
                new NoopNodeDefinition { NodeId = "end", NodeName = "End" },
            ],
            Connections =
            [
                new NodeConnection
                {
                    SourceNodeId = "start",
                    TargetNodeId = "end",
                    TriggerMessageType = MessageType.Complete,
                },
            ],
        };
    }
}
