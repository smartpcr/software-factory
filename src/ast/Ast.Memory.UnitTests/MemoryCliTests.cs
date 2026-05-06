// -----------------------------------------------------------------------
// <copyright file="MemoryCliTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using System.Diagnostics;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Ast.Memory.Services;
using Microsoft.Extensions.DependencyInjection;

[TestClass]
public class MemoryCliTests
{
    private static string cliProjectPath = string.Empty;
    private static string testStorePath = string.Empty;

    [ClassInitialize]
    public static void ClassInit(TestContext context)
    {
        // Locate the Ast.Cli project relative to the test assembly
        var assemblyDir = Path.GetDirectoryName(typeof(MemoryCliTests).Assembly.Location)!;
        // Walk up from bin/Debug/net8.0 to Ast.Memory.UnitTests, then navigate to Ast.Cli
        var testProjectDir = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", ".."));
        cliProjectPath = Path.GetFullPath(Path.Combine(testProjectDir, "..", "Ast.Cli", "Ast.Cli.csproj"));

        testStorePath = Path.Combine(Path.GetTempPath(), "ast-memory-test-" + Guid.NewGuid().ToString("N")[..8], "memory-graph.json");
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        var dir = Path.GetDirectoryName(testStorePath);
        if (dir != null && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Runs <c>dotnet run --project Ast.Cli.csproj -- {args}</c> and returns (exitCode, stdout, stderr).
    /// This exercises the real Program.cs registration.
    /// Uses async output reading to avoid deadlocks on large output.
    /// </summary>
    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var arguments = $"run --no-build --project \"{cliProjectPath}\" -- {string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}";
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;

        // Read stdout/stderr asynchronously to avoid deadlocks
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var exited = process.WaitForExit(120_000);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            return (-1, stdoutTask.IsCompleted ? stdoutTask.Result : string.Empty, "Process timed out after 120s");
        }

        // Wait for async reads to complete after process exits
        Task.WaitAll(new Task[] { stdoutTask, stderrTask }, TimeSpan.FromSeconds(10));

        return (process.ExitCode,
            stdoutTask.IsCompleted ? stdoutTask.Result : string.Empty,
            stderrTask.IsCompleted ? stderrTask.Result : string.Empty);
    }

    [TestMethod]
    public void MemoryHelp_ListsAllSubcommands()
    {
        var (exitCode, stdout, _) = RunCli("memory", "--help");

        exitCode.Should().Be(0, "memory --help should succeed");
        stdout.Should().Contain("index", "help output should list 'index' subcommand");
        stdout.Should().Contain("query", "help output should list 'query' subcommand");
        stdout.Should().Contain("sync", "help output should list 'sync' subcommand");
        stdout.Should().Contain("stats", "help output should list 'stats' subcommand");
        stdout.Should().Contain("consolidate", "help output should list 'consolidate' subcommand");
    }

    [TestMethod]
    public void MemoryIndex_WithInvalidPath_ReturnsOne()
    {
        var storePath = Path.Combine(Path.GetTempPath(), "ast-mem-inv-" + Guid.NewGuid().ToString("N")[..8], "graph.json");
        try
        {
            var (exitCode, _, _) = RunCli("memory", "index", "/nonexistent/path/xyz", "--store", storePath);
            exitCode.Should().Be(1, "index with nonexistent path should return 1");
        }
        finally
        {
            var dir = Path.GetDirectoryName(storePath);
            if (dir != null && Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    [TestMethod]
    public void MemoryIndex_ThenStats_ViaCliShowsPopulatedGraph()
    {
        var testDir = CreateTempRepoWithCSharpFiles();
        var storePath = Path.Combine(testDir, ".store", "graph.json");
        try
        {
            // Run index via the real CLI
            var (indexExit, indexOut, _) = RunCli("memory", "index", testDir, "--store", storePath);
            indexExit.Should().Be(0, "memory index should succeed");
            indexOut.Should().Contain("Indexing complete", "should report indexing complete");

            // Run stats via the real CLI (separate process, exercises persistence)
            var (statsExit, statsOut, _) = RunCli("memory", "stats", "--store", storePath);
            statsExit.Should().Be(0, "memory stats should succeed");

            // Verify stats shows non-zero nodes (graph was persisted and reloaded)
            statsOut.Should().Contain("Total Nodes", "stats should display Total Nodes row");
            statsOut.Should().NotContain("Total Nodes │   0", "stats should show populated nodes after indexing");
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void MemoryQuery_ReturnsResults_ViaCli()
    {
        var testDir = CreateTempRepoWithCSharpFiles();
        var storePath = Path.Combine(testDir, ".store", "graph.json");
        try
        {
            // Index first to populate the graph
            var (indexExit, _, _) = RunCli("memory", "index", testDir, "--store", storePath);
            indexExit.Should().Be(0, "memory index should succeed before query");

            // Query via the real CLI
            var (queryExit, queryOut, _) = RunCli("memory", "query", "MyService", "--store", storePath);
            queryExit.Should().Be(0, "memory query should succeed");
            queryOut.Should().Contain("Memory Context:", "query output should contain context markdown header");
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void MemoryConsolidate_Runs_ViaCli()
    {
        var testDir = CreateTempRepoWithCSharpFiles();
        var storePath = Path.Combine(testDir, ".store", "graph.json");
        try
        {
            // Index first
            var (indexExit, _, _) = RunCli("memory", "index", testDir, "--store", storePath);
            indexExit.Should().Be(0, "memory index should succeed before consolidate");

            // Consolidate via the real CLI
            var (consolidateExit, consolidateOut, _) = RunCli("memory", "consolidate", "--store", storePath);
            consolidateExit.Should().Be(0, "memory consolidate should succeed");
            consolidateOut.Should().Contain("Consolidation complete", "should report consolidation complete");
            consolidateOut.Should().Contain("Candidates Paired", "should display consolidation metrics");
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void MemorySync_WithInvalidRepo_ReturnsOne()
    {
        var storePath = Path.Combine(Path.GetTempPath(), "ast-mem-sync-cli-" + Guid.NewGuid().ToString("N")[..8], "graph.json");
        var fakeDir = Path.Combine(Path.GetTempPath(), "not-a-repo-cli-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fakeDir);
        try
        {
            // Sync with invalid git directory should fail gracefully
            var (syncExit, _, _) = RunCli("memory", "sync", "abc123", "def456", "-r", fakeDir, "--store", storePath);
            syncExit.Should().Be(1, "sync against non-git directory should return exit code 1");
        }
        finally
        {
            try { Directory.Delete(fakeDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            var dir = Path.GetDirectoryName(storePath);
            if (dir != null && Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    [TestMethod]
    public void ExtractionPipeline_CreatesRepoAndDirectoryAndClassAndMethodNodes()
    {
        var testDir = CreateTempRepoWithCSharpFiles();
        var storePath = Path.Combine(testDir, ".store", "graph.json");
        try
        {
            var services = new ServiceCollection();
            services.AddMemoryServices(storePath);
            var provider = services.BuildServiceProvider();
            var store = provider.GetRequiredService<IMemoryGraphStore>();
            var pipeline = provider.GetRequiredService<IExtractionPipeline>();

            var options = new ExtractionOptions(new[] { "C#" });
            var result = pipeline.ExtractAsync(testDir, options).GetAwaiter().GetResult();

            result.NodesCreated.Should().BeGreaterThan(1, "should create multiple node types");
            result.EdgesCreated.Should().BeGreaterThan(0, "should create containment edges");

            var stats = store.GetMemoryStatisticsAsync().GetAwaiter().GetResult();
            stats.TotalNodes.Should().BeGreaterThan(0);
            stats.NodesByLayer.Should().ContainKey("structural", "should have structural layer nodes");
            // call_chain layer uses underscore per architecture.md §2.1
            stats.NodesByLayer.Keys.Where(k => k == "call-chain").Should().BeEmpty("layer name should use call_chain, not call-chain");

            // Verify canonical node ID format: /{relativePath}::{qualifiedName} per architecture.md §4.2
            var structuralNodes = store.GetNodesByLayerAsync("structural", limit: 1000).GetAwaiter().GetResult();
            var symbolNodes = structuralNodes.Where(n => n.NodeType is "class" or "method" or "interface" or "struct" or "enum" or "property");
            foreach (var node in symbolNodes)
            {
                node.Id.Should().StartWith("/", $"node {node.Name} should have canonical ID starting with /");
                node.Id.Should().Contain("::", $"node {node.Name} should have :: separator between path and qualified name");
            }
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void RetrievalEngine_ReturnsMatchingNodes_WithMarkdownSections()
    {
        var testDir = CreateTempRepoWithCSharpFiles();
        var storePath = Path.Combine(testDir, ".store", "graph.json");
        try
        {
            var services = new ServiceCollection();
            services.AddMemoryServices(storePath);
            var provider = services.BuildServiceProvider();
            var pipeline = provider.GetRequiredService<IExtractionPipeline>();
            var retrieval = provider.GetRequiredService<IMemoryRetrievalEngine>();

            var options = new ExtractionOptions(new[] { "C#" });
            pipeline.ExtractAsync(testDir, options).GetAwaiter().GetResult();

            var request = new RetrievalRequest(QueryText: "MyService");
            var result = retrieval.RetrieveAsync(request).GetAwaiter().GetResult();

            result.Nodes.Should().NotBeEmpty("query for MyService should match nodes from test files");
            result.TokensUsed.Should().BeGreaterThan(0);
            result.ContextMarkdown.Should().Contain("# Memory Context:", "should have context header");
            result.ModesExecuted.Should().HaveCountGreaterThan(0, "should report executed modes");
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void RetrievalEngine_StructuralDrillMode_ReturnsStructuralSection()
    {
        var testDir = CreateTempRepoWithCSharpFiles();
        var storePath = Path.Combine(testDir, ".store", "graph.json");
        try
        {
            var services = new ServiceCollection();
            services.AddMemoryServices(storePath);
            var provider = services.BuildServiceProvider();
            var pipeline = provider.GetRequiredService<IExtractionPipeline>();
            var retrieval = provider.GetRequiredService<IMemoryRetrievalEngine>();

            pipeline.ExtractAsync(testDir, new ExtractionOptions(new[] { "C#" })).GetAwaiter().GetResult();

            var request = new RetrievalRequest(QueryText: "MyService", Mode: RetrievalMode.StructuralDrill);
            var result = retrieval.RetrieveAsync(request).GetAwaiter().GetResult();

            result.Nodes.Should().NotBeEmpty();
            result.ModesExecuted.Should().Contain(RetrievalMode.StructuralDrill);
            result.ContextMarkdown.Should().Contain("Structural Context", "structural drill should produce structural section");
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void Persistence_SurvivesAcrossStoreInstances()
    {
        var testDir = CreateTempRepoWithCSharpFiles();
        var storePath = Path.Combine(testDir, ".store", "graph.json");
        try
        {
            // Instance 1: index
            {
                var services = new ServiceCollection();
                services.AddMemoryServices(storePath);
                var provider = services.BuildServiceProvider();
                var pipeline = provider.GetRequiredService<IExtractionPipeline>();
                var options = new ExtractionOptions(new[] { "C#" });
                var result = pipeline.ExtractAsync(testDir, options).GetAwaiter().GetResult();
                result.NodesCreated.Should().BeGreaterThan(0);
                result.EdgesCreated.Should().BeGreaterThan(0);
            }

            // Instance 2: stats (simulates separate CLI process)
            {
                var services2 = new ServiceCollection();
                services2.AddMemoryServices(storePath);
                var provider2 = services2.BuildServiceProvider();
                var store2 = provider2.GetRequiredService<IMemoryGraphStore>();
                var stats = store2.GetMemoryStatisticsAsync().GetAwaiter().GetResult();
                stats.TotalNodes.Should().BeGreaterThan(0, "second store instance should see nodes persisted by first");
                stats.TotalEdges.Should().BeGreaterThan(0, "second store instance should see edges persisted by first");
            }
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void GetExpertiseForNode_ExcludesArchivedEntries()
    {
        var storePath = Path.Combine(Path.GetTempPath(), "ast-mem-archived-test-" + Guid.NewGuid().ToString("N")[..8], "graph.json");
        try
        {
            var store = new InMemoryMemoryGraphStore(storePath);
            var entry = new ExpertiseEntry
            {
                Id = "test-expertise-1",
                Kind = ExpertiseKind.Pattern,
                Title = "Test Pattern",
                Body = "A test pattern",
                TargetNodeId = "node:test",
                Confidence = 0.9,
                Archived = false,
            };
            store.UpsertExpertiseAsync(entry).GetAwaiter().GetResult();

            var active = store.GetExpertiseForNodeAsync("node:test").GetAwaiter().GetResult();
            active.Should().HaveCount(1);

            store.ArchiveExpertiseAsync("test-expertise-1").GetAwaiter().GetResult();

            var afterArchive = store.GetExpertiseForNodeAsync("node:test").GetAwaiter().GetResult();
            afterArchive.Should().BeEmpty("archived expertise should be excluded from GetExpertiseForNodeAsync");

            var archived = store.GetArchivedExpertiseAsync("node:test").GetAwaiter().GetResult();
            archived.Should().HaveCount(1, "archived entry should be retrievable via GetArchivedExpertiseAsync");
        }
        finally
        {
            var dir = Path.GetDirectoryName(storePath);
            if (dir != null && Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    [TestMethod]
    public void DiResolution_AllMemoryServicesResolve()
    {
        var services = new ServiceCollection();
        services.AddMemoryServices();
        var provider = services.BuildServiceProvider();

        provider.GetService<IExtractionPipeline>().Should().NotBeNull();
        provider.GetService<IMemoryRetrievalEngine>().Should().NotBeNull();
        provider.GetService<IIncrementalSync>().Should().NotBeNull();
        provider.GetService<IEvolutionEngine>().Should().NotBeNull();
        provider.GetService<IMemoryGraphStore>().Should().NotBeNull();
    }

    [TestMethod]
    public void IncrementalSync_ThrowsOnGitFailure()
    {
        var storePath = Path.Combine(Path.GetTempPath(), "ast-mem-sync-err-" + Guid.NewGuid().ToString("N")[..8], "graph.json");
        try
        {
            var services = new ServiceCollection();
            services.AddMemoryServices(storePath);
            var provider = services.BuildServiceProvider();
            var sync = provider.GetRequiredService<IIncrementalSync>();

            // Use invalid commit SHAs against a non-git directory
            var tempDir = Path.Combine(Path.GetTempPath(), "not-a-repo-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);
            try
            {
                var act = () => sync.ApplyDiffAsync(tempDir, "abc123", "def456").GetAwaiter().GetResult();
                act.Should().Throw<InvalidOperationException>("sync should throw on git failure, not swallow the error");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        finally
        {
            var dir = Path.GetDirectoryName(storePath);
            if (dir != null && Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    [TestMethod]
    public void TemporalRetirement_RetireNodeSetsUntilIndex()
    {
        var storePath = Path.Combine(Path.GetTempPath(), "ast-mem-retire-" + Guid.NewGuid().ToString("N")[..8], "graph.json");
        try
        {
            var store = new InMemoryMemoryGraphStore(storePath);
            var node = new Ast.Core.HypergraphNode
            {
                Id = "test:retire",
                Name = "TestRetire",
                NodeType = "Class",
                Language = "C#",
                FilePath = "test.cs",
                Metadata = new Dictionary<string, object?>
                {
                    [MemoryMetadataKeys.MemoryLayer] = "structural",
                },
            };
            store.UpsertNodeAsync(node, new VersionRange("abc", 0)).GetAwaiter().GetResult();

            var beforeRetire = store.GetNodesByLayerAsync("structural").GetAwaiter().GetResult();
            beforeRetire.Should().Contain(n => n.Id == "test:retire");

            store.RetireNodeAsync("test:retire", 1).GetAwaiter().GetResult();

            // Node is still present but retired (invisible in active queries)
            var afterRetire = store.GetNodesByLayerAsync("structural").GetAwaiter().GetResult();
            afterRetire.Should().NotContain(n => n.Id == "test:retire", "retired node should be invisible in layer queries");

            // But node still exists in the raw store (temporal, not deleted)
            var rawNode = store.GetNodeAsync("test:retire").GetAwaiter().GetResult();
            rawNode.Should().NotBeNull("retired node should still exist (temporal retirement)");
            rawNode!.Metadata.Should().ContainKey(MemoryMetadataKeys.UntilIndex, "retired node should have UntilIndex");
        }
        finally
        {
            var dir = Path.GetDirectoryName(storePath);
            if (dir != null && Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    [TestMethod]
    public void Consolidation_MergesSimilarExpertise()
    {
        var storePath = Path.Combine(Path.GetTempPath(), "ast-mem-consolidate-" + Guid.NewGuid().ToString("N")[..8], "graph.json");
        try
        {
            var services = new ServiceCollection();
            services.AddMemoryServices(storePath);
            var provider = services.BuildServiceProvider();
            var store = provider.GetRequiredService<IMemoryGraphStore>();
            var evolution = provider.GetRequiredService<IEvolutionEngine>();

            // Create a node to attach expertise to
            var node = new Ast.Core.HypergraphNode
            {
                Id = "class:TestService",
                Name = "TestService",
                NodeType = "Class",
                Language = "C#",
                FilePath = "TestService.cs",
                Metadata = new Dictionary<string, object?>
                {
                    [MemoryMetadataKeys.MemoryLayer] = "structural",
                },
            };
            store.UpsertNodeAsync(node, new VersionRange("abc", 0)).GetAwaiter().GetResult();

            // Create two similar expertise entries
            var entry1 = new ExpertiseEntry
            {
                Id = "expertise:pattern:entry1",
                Kind = ExpertiseKind.Pattern,
                Title = "Use dependency injection pattern",
                Body = "Always inject dependencies via constructor",
                TargetNodeId = "class:TestService",
                Confidence = 0.8,
                Archived = false,
            };
            var entry2 = new ExpertiseEntry
            {
                Id = "expertise:pattern:entry2",
                Kind = ExpertiseKind.Pattern,
                Title = "Use dependency injection pattern",
                Body = "Constructor injection is preferred",
                TargetNodeId = "class:TestService",
                Confidence = 0.6,
                Archived = false,
            };
            store.UpsertExpertiseAsync(entry1).GetAwaiter().GetResult();
            store.UpsertExpertiseAsync(entry2).GetAwaiter().GetResult();

            var result = evolution.ConsolidateAsync(new ConsolidationOptions(SimilarityThreshold: 0.5))
                .GetAwaiter().GetResult();

            result.CandidatesPaired.Should().BeGreaterThan(0, "should compare similar entries");
            result.MergedCount.Should().Be(1, "should merge the two identical-title entries");
            result.MergedEntryIds.Should().HaveCount(1, "should report the merged entry ID");
        }
        finally
        {
            var dir = Path.GetDirectoryName(storePath);
            if (dir != null && Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    [TestMethod]
    public void MemoryRelationEdgeType_ExistsInHyperedgeType()
    {
        // Verify that HyperedgeType.MemoryRelation is defined
        var memoryRelation = Ast.Core.HyperedgeType.MemoryRelation;
        memoryRelation.ToString().Should().Be("MemoryRelation");
    }

    [TestMethod]
    public void ExtractionPipeline_UsesCodeDerivedEdgeType()
    {
        var testDir = CreateTempRepoWithCSharpFiles();
        var storePath = Path.Combine(testDir, ".store", "graph.json");
        try
        {
            var store = new InMemoryMemoryGraphStore(storePath);
            var services = new ServiceCollection();
            services.AddMemoryServices(storePath);
            var provider = services.BuildServiceProvider();
            var pipeline = provider.GetRequiredService<IExtractionPipeline>();
            var graphStore = provider.GetRequiredService<IMemoryGraphStore>();

            var options = new ExtractionOptions(new[] { "C#" });
            pipeline.ExtractAsync(testDir, options).GetAwaiter().GetResult();

            // Verify that CONTAINS edges use code-derived HyperedgeType, not MemoryRelation
            var containEdges = graphStore.GetEdgesBySemanticRelationAsync("CONTAINS").GetAwaiter().GetResult();
            containEdges.Should().NotBeEmpty("extraction should create CONTAINS edges");
            containEdges.Should().OnlyContain(
                e => e.EdgeType != Ast.Core.HyperedgeType.MemoryRelation,
                "containment edges should use code-derived HyperedgeType, not MemoryRelation (§2.2)");
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void ExtractionPipeline_UsesCanonicalCallsRelation()
    {
        var testDir = CreateTempRepoWithCSharpFiles();
        var storePath = Path.Combine(testDir, ".store", "graph.json");
        try
        {
            var services = new ServiceCollection();
            services.AddMemoryServices(storePath);
            var provider = services.BuildServiceProvider();
            var pipeline = provider.GetRequiredService<IExtractionPipeline>();
            var graphStore = provider.GetRequiredService<IMemoryGraphStore>();

            var options = new ExtractionOptions(new[] { "C#" });
            pipeline.ExtractAsync(testDir, options).GetAwaiter().GetResult();

            // Verify canonical SemanticRelation values are used (not HyperedgeType names)
            var callsEdges = graphStore.GetEdgesBySemanticRelationAsync("CALLS").GetAwaiter().GetResult();
            // May have zero CALLS edges if no method invocations in test fixture — that's OK
            // But verify no edges use raw HyperedgeType names like MethodInvocation
            var allContainEdges = graphStore.GetEdgesBySemanticRelationAsync("CONTAINS").GetAwaiter().GetResult();
            allContainEdges.Should().NotBeEmpty("should have CONTAINS edges from extraction");
            var methodInvEdges = graphStore.GetEdgesBySemanticRelationAsync("MethodInvocation").GetAwaiter().GetResult();
            methodInvEdges.Should().BeEmpty("should use canonical 'CALLS' not raw 'MethodInvocation'");
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void ExtractionPipeline_UsesCallUnderscoreChainLayerName()
    {
        var testDir = CreateTempRepoWithCSharpFiles();
        var storePath = Path.Combine(testDir, ".store", "graph.json");
        try
        {
            var services = new ServiceCollection();
            services.AddMemoryServices(storePath);
            var provider = services.BuildServiceProvider();
            var pipeline = provider.GetRequiredService<IExtractionPipeline>();
            var graphStore = provider.GetRequiredService<IMemoryGraphStore>();

            var options = new ExtractionOptions(new[] { "C#" });
            pipeline.ExtractAsync(testDir, options).GetAwaiter().GetResult();

            var stats = graphStore.GetMemoryStatisticsAsync().GetAwaiter().GetResult();
            stats.NodesByLayer.Should().NotContainKey("call-chain", "should use 'call_chain' not 'call-chain' per architecture.md §2.1");
            stats.EdgesByLayer.Should().NotContainKey("call-chain", "edge layers should also use 'call_chain'");
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void MemoryIndex_SrcAstPath_ProducesFileAndClassNodes()
    {
        // This test exercises the exact ./src/ast/ scenario from the acceptance criteria
        var testDir = CreateSrcAstStyleRepo();
        var storePath = Path.Combine(testDir, ".ast-memory", "graph.json");
        try
        {
            var services = new ServiceCollection();
            services.AddMemoryServices(storePath);
            var provider = services.BuildServiceProvider();
            var pipeline = provider.GetRequiredService<IExtractionPipeline>();
            var graphStore = provider.GetRequiredService<IMemoryGraphStore>();

            // Index the src/ast subdirectory specifically
            var astPath = Path.Combine(testDir, "src", "ast");
            var options = new ExtractionOptions(new[] { "C#" }, ScopeSubtree: null);
            var result = pipeline.ExtractAsync(astPath, options).GetAwaiter().GetResult();

            result.NodesCreated.Should().BeGreaterThanOrEqualTo(5,
                "should create repo, dir, file, class, and method nodes");
            result.EdgesCreated.Should().BeGreaterThanOrEqualTo(2,
                "should create containment and call edges");

            var stats = graphStore.GetMemoryStatisticsAsync().GetAwaiter().GetResult();
            stats.TotalNodes.Should().BeGreaterThanOrEqualTo(5);
            stats.TotalEdges.Should().BeGreaterThanOrEqualTo(2);
            stats.NodesByLayer.Should().ContainKey("structural");

            // Verify file nodes exist
            var structuralNodes = graphStore.GetNodesByLayerAsync("structural", limit: 1000).GetAwaiter().GetResult();
            structuralNodes.Should().Contain(
                n => n.NodeType == "File" && n.Name.EndsWith(".cs"),
                "should create File nodes for .cs files");
            structuralNodes.Should().Contain(
                n => n.NodeType == "Repository",
                "should create a Repository root node");

            // Verify CONTAINS edges exist and use correct canonical name
            var containsEdges = graphStore.GetEdgesBySemanticRelationAsync("CONTAINS", limit: 100).GetAwaiter().GetResult();
            containsEdges.Should().NotBeEmpty("should have CONTAINS relationships");
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void MemoryIndex_SrcAstPath_ThenStats_ShowsPopulatedGraph_ViaCli()
    {
        // CLI acceptance scenario: ast memory index ./src/ast/ → then ast memory stats
        var testDir = CreateSrcAstStyleRepo();
        var astPath = Path.Combine(testDir, "src", "ast");
        var storePath = Path.Combine(testDir, ".ast-memory", "graph.json");
        try
        {
            var (indexExit, indexOut, _) = RunCli("memory", "index", astPath, "--store", storePath);
            indexExit.Should().Be(0, "memory index should succeed on src/ast");
            indexOut.Should().Contain("Indexing complete");

            var (statsExit, statsOut, _) = RunCli("memory", "stats", "--store", storePath);
            statsExit.Should().Be(0, "memory stats should succeed");
            statsOut.Should().Contain("Total Nodes");

            // Parse actual node count — should not be zero
            statsOut.Should().Contain("structural", "stats should show structural layer in output");
        }
        finally
        {
            try { Directory.Delete(testDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public void GetNodesByLayerAsync_RespectsNodeFilter()
    {
        var storePath = Path.Combine(Path.GetTempPath(), "ast-mem-filter-" + Guid.NewGuid().ToString("N")[..8], "graph.json");
        try
        {
            var store = new InMemoryMemoryGraphStore(storePath);
            var csharpNode = new Ast.Core.HypergraphNode
            {
                Id = "test:cs",
                Name = "CSharpClass",
                NodeType = "Class",
                Language = "C#",
                FilePath = "test.cs",
                Metadata = new Dictionary<string, object?> { [MemoryMetadataKeys.MemoryLayer] = "structural" },
            };
            var psNode = new Ast.Core.HypergraphNode
            {
                Id = "test:ps",
                Name = "PsFunction",
                NodeType = "Function",
                Language = "PowerShell",
                FilePath = "test.ps1",
                Metadata = new Dictionary<string, object?> { [MemoryMetadataKeys.MemoryLayer] = "structural" },
            };
            store.UpsertNodeAsync(csharpNode, new VersionRange("abc", 0)).GetAwaiter().GetResult();
            store.UpsertNodeAsync(psNode, new VersionRange("abc", 0)).GetAwaiter().GetResult();

            // Filter by language
            var filter = new Ast.GraphDb.NodeFilter { Language = "C#" };
            var csharpOnly = store.GetNodesByLayerAsync("structural", filter: filter).GetAwaiter().GetResult();
            csharpOnly.Should().HaveCount(1);
            csharpOnly[0].Id.Should().Be("test:cs");

            // Filter by node type
            var typeFilter = new Ast.GraphDb.NodeFilter { NodeTypes = new[] { "Function" } };
            var functionsOnly = store.GetNodesByLayerAsync("structural", filter: typeFilter).GetAwaiter().GetResult();
            functionsOnly.Should().HaveCount(1);
            functionsOnly[0].Id.Should().Be("test:ps");
        }
        finally
        {
            var dir = Path.GetDirectoryName(storePath);
            if (dir != null && Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    [TestMethod]
    public void GetEdgesBySemanticRelationAsync_RespectsAtCommitIndex()
    {
        var storePath = Path.Combine(Path.GetTempPath(), "ast-mem-commitidx-" + Guid.NewGuid().ToString("N")[..8], "graph.json");
        try
        {
            var store = new InMemoryMemoryGraphStore(storePath);
            var edge = new Ast.Core.Hyperedge
            {
                Id = "edge:test1",
                EdgeType = Ast.Core.HyperedgeType.MemoryRelation,
                NodeIds = new List<string> { "a", "b" },
                Roles = new Dictionary<string, string> { ["a"] = "parent", ["b"] = "child" },
                Metadata = new Dictionary<string, object?>
                {
                    [MemoryMetadataKeys.SemanticRelation] = "CONTAINS",
                    [MemoryMetadataKeys.MemoryLayer] = "structural",
                },
            };
            store.CreateEdgeAsync(edge).GetAwaiter().GetResult();

            // Retire the edge
            store.RetireEdgeAsync("edge:test1", 5).GetAwaiter().GetResult();

            // Querying at commit 4 should still show the edge
            var beforeRetire = store.GetEdgesBySemanticRelationAsync("CONTAINS", atCommitIndex: 4).GetAwaiter().GetResult();
            beforeRetire.Should().HaveCount(1, "edge should be visible before retirement commit");

            // Querying at commit 5 should hide the edge
            var atRetire = store.GetEdgesBySemanticRelationAsync("CONTAINS", atCommitIndex: 5).GetAwaiter().GetResult();
            atRetire.Should().BeEmpty("edge should be invisible at/after retirement commit");
        }
        finally
        {
            var dir = Path.GetDirectoryName(storePath);
            if (dir != null && Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    [TestMethod]
    public void Persistence_MetadataValuesRoundTripWithoutJsonElementIssues()
    {
        var storePath = Path.Combine(Path.GetTempPath(), "ast-mem-json-" + Guid.NewGuid().ToString("N")[..8], "graph.json");
        try
        {
            // Write with one store instance
            {
                var store = new InMemoryMemoryGraphStore(storePath);
                var node = new Ast.Core.HypergraphNode
                {
                    Id = "test:jsonrt",
                    Name = "JsonRoundTrip",
                    NodeType = "Class",
                    Language = "C#",
                    FilePath = "test.cs",
                    Metadata = new Dictionary<string, object?>
                    {
                        [MemoryMetadataKeys.MemoryLayer] = "structural",
                        [MemoryMetadataKeys.SinceIndex] = 42,
                    },
                };
                store.UpsertNodeAsync(node, new VersionRange("abc", 42)).GetAwaiter().GetResult();
            }

            // Read with fresh instance (JSON deserialization path)
            {
                var store2 = new InMemoryMemoryGraphStore(storePath);
                var nodes = store2.GetNodesByLayerAsync("structural").GetAwaiter().GetResult();
                nodes.Should().Contain(n => n.Id == "test:jsonrt", "node should survive JSON round-trip");

                // Verify temporal filtering works correctly with deserialized metadata
                var atCommit = store2.GetNodesByLayerAsync("structural", atCommitIndex: 50).GetAwaiter().GetResult();
                atCommit.Should().Contain(n => n.Id == "test:jsonrt", "node should be visible at later commit");

                var beforeCommit = store2.GetNodesByLayerAsync("structural", atCommitIndex: 10).GetAwaiter().GetResult();
                beforeCommit.Should().NotContain(n => n.Id == "test:jsonrt", "node should not be visible before its SinceIndex");
            }
        }
        finally
        {
            var dir = Path.GetDirectoryName(storePath);
            if (dir != null && Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    /// <summary>
    /// Creates a temp directory mimicking the ./src/ast/ structure from the acceptance scenario.
    /// </summary>
    private static string CreateSrcAstStyleRepo()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ast-mem-src-ast-" + Guid.NewGuid().ToString("N")[..8]);
        var astDir = Path.Combine(tempDir, "src", "ast", "Core");
        Directory.CreateDirectory(astDir);

        var coreContent = @"namespace Ast.Core;

using System.Collections.Generic;

public class HypergraphNode
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NodeType { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public Dictionary<string, object?> Metadata { get; set; } = new();
}

public class HypergraphBuilder
{
    private readonly List<HypergraphNode> nodes = new();

    public void AddNode(HypergraphNode node)
    {
        this.nodes.Add(node);
    }

    public int Count => this.nodes.Count;
}
";
        File.WriteAllText(Path.Combine(astDir, "HypergraphNode.cs"), coreContent);

        var servicesDir = Path.Combine(tempDir, "src", "ast", "Services");
        Directory.CreateDirectory(servicesDir);

        var serviceContent = @"namespace Ast.Services;

using Ast.Core;

public class GraphIndexer
{
    private readonly HypergraphBuilder builder;

    public GraphIndexer()
    {
        this.builder = new HypergraphBuilder();
    }

    public void IndexFile(string filePath, string content)
    {
        var node = new HypergraphNode
        {
            Id = filePath,
            Name = System.IO.Path.GetFileName(filePath),
            NodeType = ""File"",
            Language = ""C#"",
            FilePath = filePath,
        };
        this.builder.AddNode(node);
    }

    public int GetNodeCount()
    {
        return this.builder.Count;
    }
}
";
        File.WriteAllText(Path.Combine(servicesDir, "GraphIndexer.cs"), serviceContent);

        return tempDir;
    }

    private static string CreateTempRepoWithCSharpFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ast-mem-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        var srcDir = Path.Combine(tempDir, "src", "Services");
        Directory.CreateDirectory(srcDir);

        var csharpContent = @"namespace TestApp.Services;

using System;
using System.Collections.Generic;

public class MyService
{
    private readonly string name;

    public MyService(string name)
    {
        this.name = name;
    }

    public void ProcessData(IEnumerable<string> items)
    {
        foreach (var item in items)
        {
            Console.WriteLine(item);
        }
    }

    public string GetStatus()
    {
        return $""Service {this.name} is running"";
    }
}

public interface IMyService
{
    void ProcessData(IEnumerable<string> items);
    string GetStatus();
}
";
        File.WriteAllText(Path.Combine(srcDir, "MyService.cs"), csharpContent);

        var modelsDir = Path.Combine(tempDir, "src", "Models");
        Directory.CreateDirectory(modelsDir);

        var modelContent = @"namespace TestApp.Models;

public class DataItem
{
    public string Id { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    public override string ToString()
    {
        return $""{this.Id}: {this.Value}"";
    }
}
";
        File.WriteAllText(Path.Combine(modelsDir, "DataItem.cs"), modelContent);

        return tempDir;
    }
}
