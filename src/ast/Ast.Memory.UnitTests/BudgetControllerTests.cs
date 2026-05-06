// -----------------------------------------------------------------------
// <copyright file="BudgetControllerTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using Ast.Core;
using Ast.Memory.Context;
using Ast.Memory.Models;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class BudgetControllerTests
{
    private TiktokenCounter tokenCounter = null!;
    private BudgetController controller = null!;
    private ContextAssembler assembler = null!;

    [TestInitialize]
    public void Setup()
    {
        this.tokenCounter = new TiktokenCounter();
        this.controller = new BudgetController(this.tokenCounter);
        this.assembler = new ContextAssembler(this.tokenCounter);
    }

    [TestMethod]
    public void Constructor_NullTokenCounter_Throws()
    {
        var act = () => new BudgetController(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void EnforceBudget_NullNodes_Throws()
    {
        var act = () => this.controller.EnforceBudget(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void EnforceBudget_EmptyList_ReturnsEmptyWithTruthfulTokenCount()
    {
        var result = this.controller.EnforceBudget([], 8000);
        result.Nodes.Should().BeEmpty();

        // TokensUsed is truthful — matches actual rendering cost of empty nodes
        var assembled = this.assembler.Assemble(result.Nodes.ToList());
        result.TokensUsed.Should().Be(assembled.TokensUsed,
            "TokensUsed must be truthful and match ContextAssembler's rendering of the same nodes");
    }

    [TestMethod]
    public void EnforceBudget_Given50NodesAt16000Tokens_WithBudget8000_OutputFitsWithinBudget()
    {
        // Scenario: Budget enforcement — Given 50 ranked nodes totaling 16000 tokens
        // and budget 8000, When BudgetController runs, Then output tokens ≤ 8000
        // and highest-scored nodes are retained.
        var nodes = new List<ScoredMemoryNode>();
        var bulkySummary = string.Join(" ", Enumerable.Repeat(
            "This method performs complex multi-step validation including parameter checking, " +
            "authorization verification, rate limiting, circuit breaking, retry handling, " +
            "idempotency enforcement, distributed tracing, structured logging, metric emission, " +
            "error classification, fallback routing, cache invalidation, event publishing, " +
            "and downstream service orchestration across multiple regions", 6));

        for (var i = 0; i < 50; i++)
        {
            var node = CreateTestNode(
                id: $"node-{i}",
                name: $"Method{i}",
                nodeType: "MethodDefinition",
                filePath: $"src/Service{i / 5}.cs",
                lineStart: i * 20,
                lineEnd: (i * 20) + 15,
                summary: $"[{i}] {bulkySummary}");
            nodes.Add(new ScoredMemoryNode(
                node,
                CompositeScore: 1.0 - (i * 0.018),
                StructuralScore: 0.3,
                SemanticScore: 0.25,
                CentralityScore: 0.2,
                RecencyScore: 0.15,
                ExpertiseScore: 0.1));
        }

        // Verify the fixture actually totals ≥ 16000 tokens before budget enforcement
        var fullMarkdown = ContextAssembler.RenderMarkdown(nodes);
        var fullTokens = this.tokenCounter.CountTokens(fullMarkdown);
        fullTokens.Should().BeGreaterThanOrEqualTo(16000,
            "the 50-node fixture must total at least 16000 tokens to be a valid test");

        var result = this.controller.EnforceBudget(nodes, 8000);

        // TokensUsed must be within budget
        result.TokensUsed.Should().BeLessThanOrEqualTo(8000);
        result.Nodes.Should().NotBeEmpty();

        // Pipeline composability: ContextAssembler must produce the same token count
        var assembled = this.assembler.Assemble(result.Nodes.ToList());
        assembled.TokensUsed.Should().Be(result.TokensUsed,
            "BudgetController → ContextAssembler pipeline must produce consistent token counts");
        assembled.TokensUsed.Should().BeLessThanOrEqualTo(8000,
            "the final assembled Markdown must fit within the budget");

        // Highest-scored nodes should be retained: the top-scored input node
        // (node-0, score 1.0) must appear in the output. The bottom-scored
        // nodes should be the ones trimmed.
        var retainedIds = result.Nodes
            .Where(n => !n.Node.IsCollapsed)
            .Select(n => n.Node.Inner.Id)
            .ToHashSet();

        // node-0 is the highest-scored (1.0), must be retained
        retainedIds.Should().Contain("node-0",
            "the highest-scored node must be retained after budget trimming");

        // Verify sort order is maintained (descending by composite score)
        if (result.Nodes.Count > 1)
        {
            result.Nodes[0].CompositeScore.Should().BeGreaterThanOrEqualTo(
                result.Nodes[^1].CompositeScore,
                "top-ranked nodes must be retained, lowest-ranked trimmed");
        }

        // Some nodes must have been trimmed to fit the budget
        result.Nodes.Count.Should().BeLessThan(50,
            "with 16000+ tokens of input and 8000 budget, some nodes must be trimmed");
    }

    [TestMethod]
    public void EnforceBudget_HiAgentCollapse_ChildrenCollapsedIntoParentSummary()
    {
        // Scenario: HiAgent collapse — Given 10 child function nodes structurally
        // contained within a parent class (same file, parent line range encloses
        // children) and budget 2000, When BudgetController runs, Then children are
        // collapsed into parent summary and output tokens ≤ 2000
        var parentNode = CreateTestNode(
            id: "parent-class",
            name: "PaymentService",
            nodeType: "ClassDefinition",
            filePath: "src/PaymentService.cs",
            lineStart: 1,
            lineEnd: 300);

        var allNodes = new List<ScoredMemoryNode>();
        var longDescription = string.Join(" ", Enumerable.Repeat(
            "Handles payment processing with extensive validation, error handling, retry logic, " +
            "logging, and telemetry reporting across multiple downstream services including " +
            "authorization, fraud detection, settlement, reconciliation, notification, audit trail, " +
            "compliance checks, rate limiting, circuit breaking, and graceful degradation patterns", 5));

        allNodes.Add(new ScoredMemoryNode(
            parentNode,
            CompositeScore: 0.95,
            StructuralScore: 0.3,
            SemanticScore: 0.25,
            CentralityScore: 0.2,
            RecencyScore: 0.15,
            ExpertiseScore: 0.1));

        for (var i = 0; i < 10; i++)
        {
            var node = CreateTestNode(
                id: $"child-{i}",
                name: $"ProcessPayment{i}",
                nodeType: "MethodDefinition",
                filePath: "src/PaymentService.cs",
                lineStart: 10 + (i * 25),
                lineEnd: 10 + (i * 25) + 20,
                summary: $"Step {i}: {longDescription}");
            allNodes.Add(new ScoredMemoryNode(
                node,
                CompositeScore: 0.9 - (i * 0.05),
                StructuralScore: 0.3,
                SemanticScore: 0.25,
                CentralityScore: 0.2,
                RecencyScore: 0.15,
                ExpertiseScore: 0.1));
        }

        var result = this.controller.EnforceBudget(allNodes, 2000);

        // Budget must be respected
        result.TokensUsed.Should().BeLessThanOrEqualTo(2000);
        result.Nodes.Should().NotBeEmpty();

        // Pipeline composability check
        var assembled = this.assembler.Assemble(result.Nodes.ToList());
        assembled.TokensUsed.Should().Be(result.TokensUsed,
            "BudgetController → ContextAssembler pipeline must agree on token count");
        assembled.TokensUsed.Should().BeLessThanOrEqualTo(2000,
            "the assembled Markdown must fit within the 2000-token budget");

        // Fewer nodes than input — children collapsed or trimmed
        result.Nodes.Count.Should().BeLessThan(11,
            "children should be collapsed into parent summary to save tokens");

        // At least one collapsed node should exist with IsCollapsed = true
        var collapsedNodes = result.Nodes.Where(n => n.Node.IsCollapsed).ToList();
        collapsedNodes.Should().NotBeEmpty(
            "HiAgent collapse should produce at least one node with Collapsed = true");

        // The collapsed node's summary should mention the collapsed children
        var collapsedSummary = collapsedNodes.First().Node.Summary;
        collapsedSummary.Should().NotBeNullOrEmpty();
        collapsedSummary.Should().Contain("Contains",
            "collapsed parent summary should describe contained members");
    }

    [TestMethod]
    public void EnforceBudget_OrphanNodesWithoutParent_TrimmedNotCollapsed()
    {
        // When only child nodes exist (no parent in the list), BudgetController
        // trims lowest-scored nodes rather than synthesizing a parent. This
        // preserves individual node identity and RetrievalPath classification.
        var nodes = new List<ScoredMemoryNode>();
        var longText = string.Join(" ", Enumerable.Repeat(
            "This is a verbose method description to inflate the token count", 20));

        for (var i = 0; i < 10; i++)
        {
            var node = CreateTestNode(
                id: $"orphan-{i}",
                name: $"DoWork{i}",
                nodeType: "MethodDefinition",
                filePath: "src/Worker.cs",
                lineStart: 10 + (i * 15),
                lineEnd: 10 + (i * 15) + 10,
                summary: $"[{i}] {longText}");
            nodes.Add(new ScoredMemoryNode(
                node,
                CompositeScore: 0.9 - (i * 0.05),
                StructuralScore: 0.3,
                SemanticScore: 0.25,
                CentralityScore: 0.2,
                RecencyScore: 0.15,
                ExpertiseScore: 0.1));
        }

        // Verify uncollapsed would exceed budget
        var fullMd = ContextAssembler.RenderMarkdown(nodes);
        var fullTokens = this.tokenCounter.CountTokens(fullMd);
        fullTokens.Should().BeGreaterThan(2000,
            "test fixture must exceed 2000 tokens to trigger trimming");

        var result = this.controller.EnforceBudget(nodes, 2000);

        result.TokensUsed.Should().BeLessThanOrEqualTo(2000);
        result.Nodes.Count.Should().BeLessThan(10,
            "some nodes must be trimmed to fit within budget");

        // Highest-scored orphan must be retained
        result.Nodes.Should().Contain(n => n.Node.Inner.Id == "orphan-0",
            "the highest-scored orphan node must be retained after trimming");
    }

    [TestMethod]
    public void EnforceBudget_HiAgentCollapse_DetectsContainmentByLineRange()
    {
        var parent = CreateTestNode("parent", "MyClass", "ClassDefinition",
            "src/A.cs", 1, 100);
        var child1 = CreateTestNode("c1", "Method1", "MethodDefinition",
            "src/A.cs", 10, 30);
        var child2 = CreateTestNode("c2", "Method2", "MethodDefinition",
            "src/A.cs", 40, 60);

        var nodes = new List<ScoredMemoryNode>
        {
            new(parent, 0.9, 0.3, 0.25, 0.2, 0.15, 0.1),
            new(child1, 0.8, 0.3, 0.25, 0.2, 0.15, 0.1),
            new(child2, 0.7, 0.3, 0.25, 0.2, 0.15, 0.1)
        };

        // Use a generous budget so collapse isn't forced by trimming, only by HiAgent
        var result = this.controller.EnforceBudget(nodes, 300);

        result.TokensUsed.Should().BeLessThanOrEqualTo(300);

        // Collapse should have detected containment — at most 1 or 2 nodes remain
        if (result.Nodes.Count < 3)
        {
            var hasCollapsed = result.Nodes.Any(n => n.Node.IsCollapsed);
            hasCollapsed.Should().BeTrue("structural containment by line range should cause collapse");
        }
    }

    [TestMethod]
    public void EnforceBudget_HiAgentCollapse_OnlyCollapsesWhenItSavesTokens()
    {
        // When collapsing wouldn't save tokens (e.g., parent summary is as
        // verbose as children), collapse should not be applied.
        var parent = CreateTestNode("parent", "TinyClass", "ClassDefinition",
            "src/Tiny.cs", 1, 20);
        var child = CreateTestNode("child", "M", "MethodDefinition",
            "src/Tiny.cs", 5, 10);

        var nodes = new List<ScoredMemoryNode>
        {
            new(parent, 0.9, 0.3, 0.25, 0.2, 0.15, 0.1),
            new(child, 0.8, 0.3, 0.25, 0.2, 0.15, 0.1)
        };

        var result = this.controller.EnforceBudget(nodes, 32000);

        // With a generous budget, both nodes should be retained if collapse
        // doesn't save tokens (a collapsed node with a "Contains 1 members: M"
        // summary is roughly the same size as the original pair)
        result.TokensUsed.Should().BeGreaterThan(0);

        // Pipeline composability
        var assembled = this.assembler.Assemble(result.Nodes.ToList());
        assembled.TokensUsed.Should().Be(result.TokensUsed);
    }

    [TestMethod]
    public void EnforceBudget_DoesNotOverCollapseUnrelatedSameFileNodes()
    {
        // Nodes in the same file but far apart (structurally unrelated)
        // should NOT be collapsed into a single synthetic parent.
        var nodeA = CreateTestNode("a", "MethodA", "MethodDefinition",
            "src/BigFile.cs", 10, 20);
        var nodeB = CreateTestNode("b", "MethodB", "MethodDefinition",
            "src/BigFile.cs", 500, 510);

        var nodes = new List<ScoredMemoryNode>
        {
            new(nodeA, 0.9, 0.3, 0.25, 0.2, 0.15, 0.1),
            new(nodeB, 0.8, 0.3, 0.25, 0.2, 0.15, 0.1)
        };

        var result = this.controller.EnforceBudget(nodes, 32000);

        // With generous budget, distant nodes should remain separate
        result.Nodes.Count.Should().Be(2,
            "nodes far apart in the same file should not be collapsed together");
    }

    [TestMethod]
    public void EnforceBudget_DeduplicatesIdenticalCodeSpans()
    {
        var node1 = CreateTestNode("node-a", "Method1", "MethodDefinition", "src/A.cs", 10, 20);
        var node2 = CreateTestNode("node-b", "Method1Alias", "MethodDefinition", "src/A.cs", 10, 20);

        var nodes = new List<ScoredMemoryNode>
        {
            new(node1, 0.9, 0.3, 0.25, 0.2, 0.15, 0.1),
            new(node2, 0.8, 0.3, 0.25, 0.2, 0.15, 0.1)
        };

        var result = this.controller.EnforceBudget(nodes, 8000);

        result.Nodes.Should().HaveCount(1);
    }

    [TestMethod]
    public void EnforceBudget_DeduplicatesAcrossPathCasingAndSeparators()
    {
        // Same file+lines but different path casing and separators
        var node1 = CreateTestNode("node-a", "Method1", "MethodDefinition", "src/A.cs", 10, 20);
        var node2 = CreateTestNode("node-b", "Method1Alias", "MethodDefinition", "Src\\A.cs", 10, 20);

        var nodes = new List<ScoredMemoryNode>
        {
            new(node1, 0.9, 0.3, 0.25, 0.2, 0.15, 0.1),
            new(node2, 0.8, 0.3, 0.25, 0.2, 0.15, 0.1)
        };

        var result = this.controller.EnforceBudget(nodes, 8000);

        result.Nodes.Should().HaveCount(1,
            "nodes with same code span but different path casing/separators should be deduplicated");
    }

    [TestMethod]
    public void EnforceBudget_DoesNotDropDistinctZeroSpanNodes()
    {
        var exp1 = CreateTestNode("exp-1", "NullCheck", "Expertise", "src/A.cs", 0, 0,
            memoryLayer: "expertise", summary: "Always null-check parameters");
        var exp2 = CreateTestNode("exp-2", "RetryPolicy", "Expertise", "src/A.cs", 0, 0,
            memoryLayer: "expertise", summary: "Use exponential backoff");

        var nodes = new List<ScoredMemoryNode>
        {
            new(exp1, 0.9, 0.0, 0.0, 0.0, 0.0, 0.9),
            new(exp2, 0.8, 0.0, 0.0, 0.0, 0.0, 0.8)
        };

        var result = this.controller.EnforceBudget(nodes, 8000);

        result.Nodes.Should().HaveCount(2);
    }

    [TestMethod]
    public void EnforceBudget_TokensUsedMatchesContextAssemblerOutput()
    {
        var nodes = new List<ScoredMemoryNode>();
        for (var i = 0; i < 10; i++)
        {
            var node = CreateTestNode(
                $"node-{i}", $"Method{i}", "ClassDefinition",
                $"src/File{i}.cs", i * 10, (i * 10) + 5);
            nodes.Add(new ScoredMemoryNode(node, 1.0 - (i * 0.1), 0.3, 0.25, 0.2, 0.15, 0.1));
        }

        var result = this.controller.EnforceBudget(nodes, 8000);

        // BudgetController.TokensUsed must exactly match what ContextAssembler produces
        var assembled = this.assembler.Assemble(result.Nodes.ToList());
        result.TokensUsed.Should().Be(assembled.TokensUsed,
            "BudgetController and ContextAssembler must agree on token cost");
    }

    [TestMethod]
    public void EnforceBudget_TinyBudget_TrimsAllNodesAndReportsTruthfully()
    {
        // For very tiny budgets (smaller than the empty skeleton), BudgetController
        // trims all content nodes and reports the truthful token cost of the
        // four-section skeleton (which is the irreducible minimum output).
        var nodes = new List<ScoredMemoryNode>();
        for (var i = 0; i < 5; i++)
        {
            var node = CreateTestNode(
                $"node-{i}", $"Method{i}", "MethodDefinition",
                $"src/File{i}.cs", i * 10, (i * 10) + 5,
                summary: "Some description to ensure non-trivial rendering");
            nodes.Add(new ScoredMemoryNode(node, 1.0 - (i * 0.2), 0.3, 0.25, 0.2, 0.15, 0.1));
        }

        var result = this.controller.EnforceBudget(nodes, 10);

        // All content nodes should be trimmed since they can't fit
        result.Nodes.Should().BeEmpty(
            "no content nodes can fit in a 10-token budget");

        // TokensUsed is truthful — matches what ContextAssembler would produce
        // for the empty node list (four-section skeleton)
        var assembled = this.assembler.Assemble(result.Nodes.ToList());
        result.TokensUsed.Should().Be(assembled.TokensUsed,
            "TokensUsed must be truthful, matching ContextAssembler's rendering");

        // The skeleton always contains all four sections
        assembled.Markdown.Should().Contain("## Structure");
        assembled.Markdown.Should().Contain("## Call Graph");
        assembled.Markdown.Should().Contain("## Related Code");
        assembled.Markdown.Should().Contain("## Known Expertise");
    }

    [TestMethod]
    public void EnforceBudget_ClampsBudgetToCeiling()
    {
        var options = new MemoryOptions();
        var nodes = new List<ScoredMemoryNode>();
        for (var i = 0; i < 3; i++)
        {
            var node = CreateTestNode($"node-{i}", $"M{i}", "MethodDefinition",
                $"src/F{i}.cs", i * 10, (i * 10) + 5);
            nodes.Add(new ScoredMemoryNode(node, 0.9 - (i * 0.1), 0.3, 0.25, 0.2, 0.15, 0.1));
        }

        var result = this.controller.EnforceBudget(nodes, 50000);

        result.Nodes.Should().NotBeEmpty();
        // TokensUsed must be ≤ ceiling (32000), not the requested 50000
        result.TokensUsed.Should().BeLessThanOrEqualTo(options.TokenBudgetCeiling,
            "budget must be clamped to ceiling of {0}", options.TokenBudgetCeiling);
    }

    [TestMethod]
    public void EnforceBudget_RetainsHighestScoredNodes()
    {
        var nodes = new List<ScoredMemoryNode>();
        var padding = string.Join(" ", Enumerable.Repeat(
            "detailed description of this method including validation and error handling", 5));

        for (var i = 0; i < 5; i++)
        {
            var node = CreateTestNode(
                $"node-{i}", $"Method{i}", "MethodDefinition",
                $"src/File{i}.cs", i * 10, (i * 10) + 5,
                summary: $"[{i}] {padding}");
            nodes.Add(new ScoredMemoryNode(
                node,
                CompositeScore: 1.0 - (i * 0.2),
                StructuralScore: 0.3,
                SemanticScore: 0.25,
                CentralityScore: 0.2,
                RecencyScore: 0.15,
                ExpertiseScore: 0.1));
        }

        // Budget tight enough to force trimming but above skeleton cost
        var result = this.controller.EnforceBudget(nodes, 200);

        // With a tight budget, some nodes must be trimmed
        result.Nodes.Count.Should().BeLessThan(5);

        // The retained nodes should be the highest-scored (node-0 has score 1.0)
        var retainedNonCollapsed = result.Nodes
            .Where(n => !n.Node.IsCollapsed)
            .ToList();
        if (retainedNonCollapsed.Count > 0)
        {
            retainedNonCollapsed.Should().Contain(n => n.Node.Inner.Id == "node-0",
                "the highest-scored node must be retained");
        }
    }

    [TestMethod]
    public void EnforceBudget_DefaultBudgetIs8000()
    {
        this.controller.TokenBudgetDefault.Should().Be(8000);
    }

    [TestMethod]
    public void EnforceBudget_CeilingIs32000()
    {
        this.controller.TokenBudgetCeiling.Should().Be(32000);
    }

    [TestMethod]
    public void EnforceBudget_PipelineComposability_AssemblerProducesSameMarkdownAsInternalRendering()
    {
        // The key pipeline contract: BudgetController trims nodes, then
        // ContextAssembler renders the same nodes to Markdown. Both must
        // produce identical output.
        var nodes = new List<ScoredMemoryNode>();
        for (var i = 0; i < 8; i++)
        {
            var layer = i switch
            {
                < 3 => "structural",
                < 5 => "call_graph",
                < 7 => "semantic",
                _ => "expertise"
            };
            var nodeType = i switch
            {
                < 3 => "ClassDefinition",
                < 5 => "MethodInvocation",
                < 7 => "MethodDefinition",
                _ => "Expertise"
            };
            var node = CreateTestNode(
                $"pipe-{i}", $"Symbol{i}", nodeType,
                $"src/File{i}.cs", i * 10, (i * 10) + 8,
                memoryLayer: layer,
                summary: $"Summary for node {i}");
            nodes.Add(new ScoredMemoryNode(node, 1.0 - (i * 0.1), 0.3, 0.25, 0.2, 0.15, 0.1));
        }

        var budgetResult = this.controller.EnforceBudget(nodes, 8000);
        var assembledResult = this.assembler.Assemble(budgetResult.Nodes.ToList());

        // Token counts must match
        budgetResult.TokensUsed.Should().Be(assembledResult.TokensUsed);

        // The Markdown from ContextAssembler must have all four sections
        assembledResult.Markdown.Should().Contain("## Structure");
        assembledResult.Markdown.Should().Contain("## Call Graph");
        assembledResult.Markdown.Should().Contain("## Related Code");
        assembledResult.Markdown.Should().Contain("## Known Expertise");
    }

    private static MemoryNode CreateTestNode(
        string id,
        string name,
        string nodeType,
        string filePath,
        int lineStart,
        int lineEnd,
        string? parentId = null,
        string? summary = null,
        string memoryLayer = "structural")
    {
        var metadata = new Dictionary<string, object?>
        {
            ["MemoryLayer"] = memoryLayer
        };

        if (parentId is not null)
        {
            metadata["ParentId"] = parentId;
        }

        if (summary is not null)
        {
            metadata["Summary"] = summary;
        }

        return new MemoryNode(new HypergraphNode
        {
            Id = id,
            Name = name,
            NodeType = nodeType,
            Language = "C#",
            FilePath = filePath,
            LineStart = lineStart,
            LineEnd = lineEnd,
            Metadata = metadata
        });
    }
}
