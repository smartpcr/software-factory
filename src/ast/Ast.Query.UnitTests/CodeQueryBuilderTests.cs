// -----------------------------------------------------------------------
// <copyright file="CodeQueryBuilderTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Query.UnitTests;

using Ast.Core;
using Ast.GraphDb;
using Microsoft.Extensions.Logging;
using Moq;

[TestClass]
public class CodeQueryBuilderTests
{
    private Mock<IGraphStore> mockGraphStore = null!;
    private Mock<ILogger<CodeQueryBuilder>> mockLogger = null!;
    private CodeQueryBuilder builder = null!;

    [TestInitialize]
    public void Setup()
    {
        this.mockGraphStore = new Mock<IGraphStore>();
        this.mockLogger = new Mock<ILogger<CodeQueryBuilder>>();
        this.builder = new CodeQueryBuilder(this.mockGraphStore.Object, this.mockLogger.Object);
    }

    [TestMethod]
    public void Constructor_WithNullGraphStore_ThrowsArgumentNullException()
    {
        var act = () => new CodeQueryBuilder(null!, this.mockLogger.Object);
        act.Should().Throw<ArgumentNullException>().WithParameterName("graphStore");
    }

    [TestMethod]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        var act = () => new CodeQueryBuilder(this.mockGraphStore.Object, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [TestMethod]
    public void Nodes_ReturnsNodeQuery()
    {
        var query = this.builder.Nodes();
        query.Should().NotBeNull();
        query.Should().BeAssignableTo<INodeQuery>();
    }

    [TestMethod]
    public void Node_ReturnsISingleNodeQuery()
    {
        var query = this.builder.Node("test-id");
        query.Should().NotBeNull();
        query.Should().BeAssignableTo<ISingleNodeQuery>();
    }

    [TestMethod]
    public void NodeByName_ReturnsISingleNodeQuery()
    {
        var query = this.builder.NodeByName("TestClass");
        query.Should().NotBeNull();
        query.Should().BeAssignableTo<ISingleNodeQuery>();
    }

    [TestMethod]
    public void PathsBetween_ReturnsIPathQuery()
    {
        var query = this.builder.PathsBetween("node1", "node2");
        query.Should().NotBeNull();
        query.Should().BeAssignableTo<IPathQuery>();
    }

    [TestMethod]
    public void PathsBetweenNames_ReturnsIPathQuery()
    {
        var query = this.builder.PathsBetweenNames("Class1", "Class2");
        query.Should().NotBeNull();
        query.Should().BeAssignableTo<IPathQuery>();
    }

    [TestMethod]
    public void Search_WithoutVectorStore_ThrowsInvalidOperationException()
    {
        var act = () => this.builder.Search("test query");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Vector store not configured*");
    }

    [TestMethod]
    public async Task Nodes_Classes_FiltersCorrectlyAsync()
    {
        var nodes = new List<HypergraphNode>
        {
            CreateTestNode("1", "Class1", "Class"),
            CreateTestNode("2", "Class2", "Class")
        };

        this.mockGraphStore.Setup(x => x.GetNodesAsync(
            It.Is<NodeFilter>(f => f.NodeTypes != null && f.NodeTypes.Contains("Class")),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(nodes);

        var result = await this.builder.Nodes().Classes().ToListAsync();

        result.Should().HaveCount(2);
        result.Should().AllSatisfy(n => n.NodeType.Should().Be("Class"));
    }

    [TestMethod]
    public async Task Nodes_Methods_FiltersCorrectlyAsync()
    {
        var nodes = new List<HypergraphNode>
        {
            CreateTestNode("1", "Method1", "Method")
        };

        this.mockGraphStore.Setup(x => x.GetNodesAsync(
            It.Is<NodeFilter>(f => f.NodeTypes != null && f.NodeTypes.Contains("Method")),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(nodes);

        var result = await this.builder.Nodes().Methods().ToListAsync();

        result.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task Nodes_OfLanguage_FiltersCorrectlyAsync()
    {
        var nodes = new List<HypergraphNode>
        {
            CreateTestNode("1", "Test1", "Class", "C#")
        };

        this.mockGraphStore.Setup(x => x.GetNodesAsync(
            It.Is<NodeFilter>(f => f.Language == "C#"),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(nodes);

        var result = await this.builder.Nodes().OfLanguage("C#").ToListAsync();

        result.Should().HaveCount(1);
        result[0].Language.Should().Be("C#");
    }

    [TestMethod]
    public async Task Nodes_WithName_FiltersCorrectlyAsync()
    {
        var nodes = new List<HypergraphNode>
        {
            CreateTestNode("1", "TestHelper", "Class")
        };

        this.mockGraphStore.Setup(x => x.GetNodesAsync(
            It.Is<NodeFilter>(f => f.NamePattern == "Test*"),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(nodes);

        var result = await this.builder.Nodes().WithName("Test*").ToListAsync();

        result.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task Nodes_InFile_FiltersCorrectlyAsync()
    {
        var nodes = new List<HypergraphNode>
        {
            CreateTestNode("1", "Test1", "Class", filePath: "/src/test.cs")
        };

        this.mockGraphStore.Setup(x => x.GetNodesAsync(
            It.Is<NodeFilter>(f => f.FilePath == "/src/test.cs"),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(nodes);

        var result = await this.builder.Nodes().InFile("/src/test.cs").ToListAsync();

        result.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task Nodes_Take_LimitsResultsAsync()
    {
        var nodes = new List<HypergraphNode>
        {
            CreateTestNode("1", "Test1", "Class"),
            CreateTestNode("2", "Test2", "Class"),
            CreateTestNode("3", "Test3", "Class")
        };

        this.mockGraphStore.Setup(x => x.GetNodesAsync(
            It.IsAny<NodeFilter>(),
            It.Is<int>(l => l == 2),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(nodes.Take(2).ToList());

        var result = await this.builder.Nodes().Take(2).ToListAsync();

        result.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task Nodes_CountAsync_ReturnsCountAsync()
    {
        var nodes = new List<HypergraphNode>
        {
            CreateTestNode("1", "Test1", "Class"),
            CreateTestNode("2", "Test2", "Class")
        };

        this.mockGraphStore.Setup(x => x.GetNodesAsync(
            It.IsAny<NodeFilter>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(nodes);

        var count = await this.builder.Nodes().CountAsync();

        count.Should().Be(2);
    }

    [TestMethod]
    public async Task Nodes_ExecuteAsync_ReturnsCodeQueryResultAsync()
    {
        var nodes = new List<HypergraphNode>
        {
            CreateTestNode("1", "Test1", "Class")
        };

        this.mockGraphStore.Setup(x => x.GetNodesAsync(
            It.IsAny<NodeFilter>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(nodes);

        var result = await this.builder.Nodes().ExecuteAsync();

        result.Should().NotBeNull();
        result.Nodes.Should().HaveCount(1);
        result.HasResults.Should().BeTrue();
    }

    [TestMethod]
    public async Task Node_GetAsync_ReturnsNodeAsync()
    {
        var node = CreateTestNode("test-id", "TestClass", "Class");

        this.mockGraphStore.Setup(x => x.GetNodeAsync("test-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(node);

        var result = await this.builder.Node("test-id").GetAsync();

        result.Should().NotBeNull();
        result!.Id.Should().Be("test-id");
    }

    [TestMethod]
    public async Task NodeByName_GetAsync_ReturnsNodeAsync()
    {
        var node = CreateTestNode("test-id", "TestClass", "Class");

        this.mockGraphStore.Setup(x => x.GetNodesAsync(
            It.Is<NodeFilter>(f => f.NamePattern == "TestClass"),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HypergraphNode> { node });

        var result = await this.builder.NodeByName("TestClass").GetAsync();

        result.Should().NotBeNull();
        result!.Name.Should().Be("TestClass");
    }

    [TestMethod]
    public async Task Node_Callers_ReturnsCallersAsync()
    {
        var callers = new List<NodeWithDepth>
        {
            new() { Node = CreateTestNode("caller1", "Caller1", "Method"), Depth = 1, RelationshipType = HyperedgeType.MethodInvocation }
        };

        this.mockGraphStore.Setup(x => x.GetCallersAsync("test-id", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(callers);

        var result = await this.builder.Node("test-id").Callers().ToListAsync();

        result.Should().HaveCount(1);
        result[0].Node.Name.Should().Be("Caller1");
    }

    [TestMethod]
    public async Task Node_Callees_ReturnsCalleesAsync()
    {
        var callees = new List<NodeWithDepth>
        {
            new() { Node = CreateTestNode("callee1", "Callee1", "Method"), Depth = 1, RelationshipType = HyperedgeType.MethodInvocation }
        };

        this.mockGraphStore.Setup(x => x.GetCalleesAsync("test-id", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(callees);

        var result = await this.builder.Node("test-id").Callees().ToListAsync();

        result.Should().HaveCount(1);
        result[0].Node.Name.Should().Be("Callee1");
    }

    [TestMethod]
    public async Task Node_Callers_WithDepth_UsesCorrectDepthAsync()
    {
        var callers = new List<NodeWithDepth>
        {
            new() { Node = CreateTestNode("c1", "Caller1", "Method"), Depth = 1, RelationshipType = HyperedgeType.MethodInvocation },
            new() { Node = CreateTestNode("c2", "Caller2", "Method"), Depth = 2, RelationshipType = HyperedgeType.MethodInvocation }
        };

        this.mockGraphStore.Setup(x => x.GetCallersAsync("test-id", 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(callers);

        var result = await this.builder.Node("test-id").Callers().WithDepth(3).ToListAsync();

        result.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task Node_Implementations_ReturnsImplementingClassesAsync()
    {
        var implementations = new List<NodeWithDepth>
        {
            new() { Node = CreateTestNode("impl1", "Impl1", "Class"), Depth = 1, RelationshipType = HyperedgeType.Implements }
        };

        this.mockGraphStore.Setup(x => x.GetCallersAsync("iface-id", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(implementations);

        var result = await this.builder.Node("iface-id").Implementations().ToListAsync();

        result.Should().HaveCount(1);
        result[0].RelationshipType.Should().Be(HyperedgeType.Implements);
    }

    [TestMethod]
    public async Task Node_DerivedClasses_ReturnsDerivedAsync()
    {
        var derived = new List<NodeWithDepth>
        {
            new() { Node = CreateTestNode("d1", "Derived1", "Class"), Depth = 1, RelationshipType = HyperedgeType.Extends }
        };

        this.mockGraphStore.Setup(x => x.GetCallersAsync("base-id", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(derived);

        var result = await this.builder.Node("base-id").DerivedClasses().ToListAsync();

        result.Should().HaveCount(1);
        result[0].RelationshipType.Should().Be(HyperedgeType.Extends);
    }

    [TestMethod]
    public async Task Node_Impact_ReturnsImpactAnalysisAsync()
    {
        var node = CreateTestNode("test-id", "TestMethod", "Method");
        var impact = new ImpactAnalysis
        {
            SourceNode = node,
            DirectlyAffected =
            [
                new() { Node = CreateTestNode("d1", "Affected1", "Method"), Depth = 1, RelationshipType = HyperedgeType.MethodInvocation }
            ]
        };

        this.mockGraphStore.Setup(x => x.GetNodesAsync(
            It.IsAny<NodeFilter>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HypergraphNode> { node });

        this.mockGraphStore.Setup(x => x.AnalyzeImpactAsync("test-id", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(impact);

        var result = await this.builder.Node("test-id").Impact().ExecuteAsync();

        result.Should().NotBeNull();
        result.DirectlyAffected.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task Node_Edges_ReturnsEdgesAsync()
    {
        var edges = new List<Hyperedge>
        {
            new() { Id = "e1", EdgeType = HyperedgeType.MethodInvocation, NodeIds = ["test-id", "other"], Roles = [] }
        };

        this.mockGraphStore.Setup(x => x.GetEdgesForNodeAsync("test-id", It.IsAny<IEnumerable<HyperedgeType>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(edges);

        var result = await this.builder.Node("test-id").Edges().ExecuteAsync();

        result.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task PathsBetween_FindsPathsAsync()
    {
        var path = new GraphPath
        {
            Nodes = { CreateTestNode("n1", "Node1", "Class"), CreateTestNode("n2", "Node2", "Class") },
            Edges = { new Hyperedge { Id = "e1", EdgeType = HyperedgeType.MethodInvocation, NodeIds = ["n1", "n2"], Roles = [] } }
        };

        this.mockGraphStore.Setup(x => x.FindPathsAsync("node1", "node2", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GraphPath> { path });

        var result = await this.builder.PathsBetween("node1", "node2").ToListAsync();

        result.Should().HaveCount(1);
        result[0].Length.Should().Be(1);
    }

    [TestMethod]
    public async Task PathsBetween_WithMaxLength_UsesCorrectDepthAsync()
    {
        var path = new GraphPath
        {
            Nodes = { CreateTestNode("n1", "Node1", "Class"), CreateTestNode("n2", "Node2", "Class") }
        };

        this.mockGraphStore.Setup(x => x.FindPathsAsync("n1", "n2", 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GraphPath> { path });

        var result = await this.builder.PathsBetween("n1", "n2").WithMaxLength(10).ToListAsync();

        result.Should().HaveCount(1);
    }

    private static HypergraphNode CreateTestNode(
        string id,
        string name,
        string nodeType,
        string language = "C#",
        string filePath = "/test/file.cs")
    {
        return new HypergraphNode
        {
            Id = id,
            Name = name,
            NodeType = nodeType,
            Language = language,
            FilePath = filePath,
            LineStart = 1,
            LineEnd = 10
        };
    }
}