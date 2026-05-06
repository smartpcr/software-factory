// -----------------------------------------------------------------------
// <copyright file="ContextAssemblerTests.cs" company="Microsoft Corp.">
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
public class ContextAssemblerTests
{
    private TiktokenCounter tokenCounter = null!;
    private ContextAssembler assembler = null!;

    [TestInitialize]
    public void Setup()
    {
        this.tokenCounter = new TiktokenCounter();
        this.assembler = new ContextAssembler(this.tokenCounter);
    }

    [TestMethod]
    public void Constructor_NullTokenCounter_Throws()
    {
        var act = () => new ContextAssembler(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void Assemble_NullNodes_Throws()
    {
        var act = () => this.assembler.Assemble(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void Assemble_EmptyNodes_ContainsAllFourSectionsInOrder()
    {
        var result = this.assembler.Assemble([]);

        result.Markdown.Should().Contain("## Structure");
        result.Markdown.Should().Contain("## Call Graph");
        result.Markdown.Should().Contain("## Related Code");
        result.Markdown.Should().Contain("## Known Expertise");

        // Verify order
        var structureIdx = result.Markdown.IndexOf("## Structure", StringComparison.Ordinal);
        var callGraphIdx = result.Markdown.IndexOf("## Call Graph", StringComparison.Ordinal);
        var relatedCodeIdx = result.Markdown.IndexOf("## Related Code", StringComparison.Ordinal);
        var expertiseIdx = result.Markdown.IndexOf("## Known Expertise", StringComparison.Ordinal);

        structureIdx.Should().BeLessThan(callGraphIdx);
        callGraphIdx.Should().BeLessThan(relatedCodeIdx);
        relatedCodeIdx.Should().BeLessThan(expertiseIdx);
    }

    [TestMethod]
    public void Assemble_NodesSpanningAllLayers_ContainsExactSectionsInOrder()
    {
        // Scenario: Context Markdown sections — Given ranked nodes spanning all 4 layers,
        // When ContextAssembler produces output, Then the Markdown contains exactly the
        // sections ## Structure, ## Call Graph, ## Related Code, ## Known Expertise in that order
        var nodes = new List<ScoredMemoryNode>
        {
            // Structural node
            CreateScoredNode("struct-1", "PaymentService", "ClassDefinition",
                "src/PaymentService.cs", 1, 100, "structural"),

            // Call graph node
            CreateScoredNode("call-1", "ProcessPayment", "MethodInvocation",
                "src/PaymentService.cs", 10, 30, "call_graph"),

            // Related code (semantic) node
            CreateScoredNode("sem-1", "RefundService", "ClassDefinition",
                "src/RefundService.cs", 1, 50, "semantic"),

            // Expertise node
            CreateScoredNode("exp-1", "NullCheckPattern", "Expertise",
                "src/PaymentService.cs", 0, 0, "expertise",
                summary: "Always check for null before processing payment")
        };

        var result = this.assembler.Assemble(nodes);

        // All four sections must be present
        result.Markdown.Should().Contain("## Structure");
        result.Markdown.Should().Contain("## Call Graph");
        result.Markdown.Should().Contain("## Related Code");
        result.Markdown.Should().Contain("## Known Expertise");

        // Verify correct order
        var indices = new[]
        {
            result.Markdown.IndexOf("## Structure", StringComparison.Ordinal),
            result.Markdown.IndexOf("## Call Graph", StringComparison.Ordinal),
            result.Markdown.IndexOf("## Related Code", StringComparison.Ordinal),
            result.Markdown.IndexOf("## Known Expertise", StringComparison.Ordinal)
        };

        for (var i = 0; i < indices.Length - 1; i++)
        {
            indices[i].Should().BeLessThan(indices[i + 1],
                "sections must appear in the specified order");
        }

        // Verify these are the ONLY level-2 headings (exactly 4 sections, no extras)
        var headingCount = 0;
        var searchStart = 0;
        while (true)
        {
            var pos = result.Markdown.IndexOf("\n## ", searchStart, StringComparison.Ordinal);
            if (pos < 0)
            {
                // Also check if the Markdown starts with "## "
                break;
            }

            headingCount++;
            searchStart = pos + 4;
        }

        // Account for Markdown starting with "## " (no preceding newline)
        if (result.Markdown.StartsWith("## ", StringComparison.Ordinal))
        {
            headingCount++;
        }

        headingCount.Should().Be(4,
            "the output must contain exactly 4 level-2 sections, no more, no less");

        // Each section should contain the corresponding node's data
        var structureSection = result.Markdown[indices[0]..indices[1]];
        structureSection.Should().Contain("PaymentService");

        var callGraphSection = result.Markdown[indices[1]..indices[2]];
        callGraphSection.Should().Contain("ProcessPayment");

        var relatedSection = result.Markdown[indices[2]..indices[3]];
        relatedSection.Should().Contain("RefundService");

        var expertiseSection = result.Markdown[indices[3]..];
        expertiseSection.Should().Contain("NullCheckPattern");
        expertiseSection.Should().Contain("Always check for null before processing payment");
    }

    [TestMethod]
    public void Assemble_StructuralNodes_GroupedByFile()
    {
        var nodes = new List<ScoredMemoryNode>
        {
            CreateScoredNode("s1", "Method1", "MethodDefinition", "src/A.cs", 10, 20, "structural"),
            CreateScoredNode("s2", "Method2", "MethodDefinition", "src/A.cs", 30, 40, "structural"),
            CreateScoredNode("s3", "ClassB", "ClassDefinition", "src/B.cs", 1, 50, "structural")
        };

        var result = this.assembler.Assemble(nodes);

        result.Markdown.Should().Contain("src/A.cs");
        result.Markdown.Should().Contain("src/B.cs");
        result.Markdown.Should().Contain("Method1");
        result.Markdown.Should().Contain("Method2");
    }

    [TestMethod]
    public void Assemble_ExpertiseNodes_ShowConfidence()
    {
        var nodes = new List<ScoredMemoryNode>
        {
            CreateScoredNode("exp-1", "ErrorHandling", "Expertise",
                "src/Service.cs", 0, 0, "expertise",
                summary: "Use try-catch for all external calls",
                expertiseScore: 0.85)
        };

        var result = this.assembler.Assemble(nodes);

        result.Markdown.Should().Contain("ErrorHandling");
        result.Markdown.Should().Contain("0.85");
        result.Markdown.Should().Contain("Use try-catch for all external calls");
    }

    [TestMethod]
    public void Assemble_ReturnsTokenCount()
    {
        var nodes = new List<ScoredMemoryNode>
        {
            CreateScoredNode("s1", "Method1", "ClassDefinition", "src/A.cs", 1, 10, "structural")
        };

        var result = this.assembler.Assemble(nodes);

        result.TokensUsed.Should().BeGreaterThan(0);
        result.Markdown.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public void Assemble_CallGraphNodes_IncludesSummary()
    {
        var nodes = new List<ScoredMemoryNode>
        {
            CreateScoredNode("cg-1", "ProcessOrder", "MethodInvocation",
                "src/OrderService.cs", 15, 30, "call_graph",
                summary: "Calls PaymentService.ProcessPayment then ShippingService.Ship")
        };

        var result = this.assembler.Assemble(nodes);

        result.Markdown.Should().Contain("ProcessOrder");
        result.Markdown.Should().Contain("Calls PaymentService.ProcessPayment");
    }

    private static ScoredMemoryNode CreateScoredNode(
        string id,
        string name,
        string nodeType,
        string filePath,
        int lineStart,
        int lineEnd,
        string memoryLayer,
        string? summary = null,
        double compositeScore = 0.5,
        double expertiseScore = 0.1)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["MemoryLayer"] = memoryLayer
        };

        if (summary is not null)
        {
            metadata["Summary"] = summary;
        }

        var inner = new HypergraphNode
        {
            Id = id,
            Name = name,
            NodeType = nodeType,
            Language = "C#",
            FilePath = filePath,
            LineStart = lineStart,
            LineEnd = lineEnd,
            Metadata = metadata
        };

        return new ScoredMemoryNode(
            new MemoryNode(inner),
            compositeScore,
            StructuralScore: 0.3,
            SemanticScore: 0.25,
            CentralityScore: 0.2,
            RecencyScore: 0.15,
            ExpertiseScore: expertiseScore);
    }
}
