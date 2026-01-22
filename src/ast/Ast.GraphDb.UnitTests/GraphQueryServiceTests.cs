// -----------------------------------------------------------------------
// <copyright file="GraphQueryServiceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.GraphDb.UnitTests;

using Ast.Core;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

[TestClass]
public class GraphQueryServiceTests
{
    private Mock<IGraphStore> mockGraphStore = null!;
    private Mock<ILogger<GraphQueryService>> mockLogger = null!;
    private GraphQueryService service = null!;

    [TestInitialize]
    public void Setup()
    {
        mockGraphStore = new Mock<IGraphStore>();
        mockLogger = new Mock<ILogger<GraphQueryService>>();
        service = new GraphQueryService(mockGraphStore.Object, mockLogger.Object);
    }

    [TestMethod]
    public void Constructor_WithNullGraphStore_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new GraphQueryService(null!, mockLogger.Object);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("graphStore");
    }

    [TestMethod]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new GraphQueryService(mockGraphStore.Object, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }

    [TestMethod]
    public async Task IndexHypergraphAsync_WithValidData_IndexesNodesAndEdges()
    {
        // Arrange
        var hypergraph = new HypergraphData
        {
            Nodes = new Dictionary<string, HypergraphNode>
            {
                ["node1"] = CreateTestNode("node1", "TestClass"),
                ["node2"] = CreateTestNode("node2", "TestMethod")
            },
            Hyperedges = new List<Hyperedge>
            {
                CreateTestEdge("edge1", HyperedgeType.MethodInvocation, new[] { "node1", "node2" })
            }
        };

        mockGraphStore.Setup(x => x.CreateNodesAsync(
            It.IsAny<IEnumerable<HypergraphNode>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        mockGraphStore.Setup(x => x.CreateEdgesAsync(
            It.IsAny<IEnumerable<Hyperedge>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Act
        var result = await service.IndexHypergraphAsync(hypergraph);

        // Assert
        result.NodesIndexed.Should().Be(2);
        result.EdgesIndexed.Should().Be(1);
    }

    [TestMethod]
    public async Task IndexHypergraphAsync_WithNullHypergraph_ThrowsArgumentNullException()
    {
        // Act
        var act = () => service.IndexHypergraphAsync(null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [TestMethod]
    public async Task FindImplementationsAsync_WhenInterfaceFound_ReturnsImplementations()
    {
        // Arrange
        var interfaceNode = CreateTestNode("iface1", "ITestInterface", "Interface");
        var classNode = CreateTestNode("class1", "TestClass", "Class");

        mockGraphStore.Setup(x => x.GetNodesAsync(
            It.Is<NodeFilter>(f => f.NodeTypes!.Contains("Interface")),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HypergraphNode> { interfaceNode });

        mockGraphStore.Setup(x => x.GetCallersAsync(
            "iface1",
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NodeWithDepth>
            {
                new NodeWithDepth
                {
                    Node = classNode,
                    Depth = 1,
                    RelationshipType = HyperedgeType.Implements
                }
            });

        // Act
        var result = await service.FindImplementationsAsync("ITestInterface");

        // Assert
        result.Should().HaveCount(1);
        result[0].Name.Should().Be("TestClass");
    }

    [TestMethod]
    public async Task FindImplementationsAsync_WhenInterfaceNotFound_ReturnsEmpty()
    {
        // Arrange
        mockGraphStore.Setup(x => x.GetNodesAsync(
            It.IsAny<NodeFilter>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HypergraphNode>());

        // Act
        var result = await service.FindImplementationsAsync("NonExistent");

        // Assert
        result.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FindImplementationsAsync_WithEmptyName_ThrowsArgumentException()
    {
        // Act
        var act = () => service.FindImplementationsAsync("");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [TestMethod]
    public async Task FindDerivedClassesAsync_WhenClassFound_ReturnsDerived()
    {
        // Arrange
        var baseClass = CreateTestNode("base1", "BaseClass", "Class");
        var derivedClass = CreateTestNode("derived1", "DerivedClass", "Class");

        mockGraphStore.Setup(x => x.GetNodesAsync(
            It.Is<NodeFilter>(f => f.NodeTypes!.Contains("Class")),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HypergraphNode> { baseClass });

        mockGraphStore.Setup(x => x.GetCallersAsync(
            "base1",
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NodeWithDepth>
            {
                new NodeWithDepth
                {
                    Node = derivedClass,
                    Depth = 1,
                    RelationshipType = HyperedgeType.Extends
                }
            });

        // Act
        var result = await service.FindDerivedClassesAsync("BaseClass");

        // Assert
        result.Should().HaveCount(1);
        result[0].Name.Should().Be("DerivedClass");
    }

    [TestMethod]
    public async Task FindCallChainAsync_WhenPathExists_ReturnsPaths()
    {
        // Arrange
        var sourceMethod = CreateTestNode("method1", "SourceMethod", "Method");
        var targetMethod = CreateTestNode("method2", "TargetMethod", "Method");
        var path = new GraphPath
        {
            Nodes = { sourceMethod, targetMethod },
            Edges = { CreateTestEdge("e1", HyperedgeType.MethodInvocation, new[] { "method1", "method2" }) }
        };

        mockGraphStore.Setup(x => x.GetNodesAsync(
            It.Is<NodeFilter>(f => f.NamePattern == "SourceMethod"),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HypergraphNode> { sourceMethod });

        mockGraphStore.Setup(x => x.GetNodesAsync(
            It.Is<NodeFilter>(f => f.NamePattern == "TargetMethod"),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HypergraphNode> { targetMethod });

        mockGraphStore.Setup(x => x.FindPathsAsync(
            "method1",
            "method2",
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GraphPath> { path });

        // Act
        var result = await service.FindCallChainAsync("SourceMethod", "TargetMethod");

        // Assert
        result.Should().HaveCount(1);
        result[0].Nodes.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task FindCallChainAsync_WhenSourceNotFound_ReturnsEmpty()
    {
        // Arrange
        mockGraphStore.Setup(x => x.GetNodesAsync(
            It.IsAny<NodeFilter>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HypergraphNode>());

        // Act
        var result = await service.FindCallChainAsync("NonExistent", "TargetMethod");

        // Assert
        result.Should().BeEmpty();
    }

    [TestMethod]
    public async Task GetMethodDependenciesAsync_ReturnsDependencies()
    {
        // Arrange
        var method = CreateTestNode("method1", "TestMethod", "Method");
        var dependency = CreateTestNode("dep1", "HelperMethod", "Method");

        mockGraphStore.Setup(x => x.GetNodesAsync(
            It.IsAny<NodeFilter>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HypergraphNode> { method });

        mockGraphStore.Setup(x => x.GetCalleesAsync(
            "method1",
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NodeWithDepth>
            {
                new NodeWithDepth
                {
                    Node = dependency,
                    Depth = 1,
                    RelationshipType = HyperedgeType.MethodInvocation
                }
            });

        // Act
        var result = await service.GetMethodDependenciesAsync("TestMethod");

        // Assert
        result.Should().HaveCount(1);
        result[0].Node.Name.Should().Be("HelperMethod");
    }

    [TestMethod]
    public async Task GetMethodCallersAsync_ReturnsCallers()
    {
        // Arrange
        var method = CreateTestNode("method1", "TestMethod", "Method");
        var caller = CreateTestNode("caller1", "CallerMethod", "Method");

        mockGraphStore.Setup(x => x.GetNodesAsync(
            It.IsAny<NodeFilter>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HypergraphNode> { method });

        mockGraphStore.Setup(x => x.GetCallersAsync(
            "method1",
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NodeWithDepth>
            {
                new NodeWithDepth
                {
                    Node = caller,
                    Depth = 1,
                    RelationshipType = HyperedgeType.MethodInvocation
                }
            });

        // Act
        var result = await service.GetMethodCallersAsync("TestMethod");

        // Assert
        result.Should().HaveCount(1);
        result[0].Node.Name.Should().Be("CallerMethod");
    }

    [TestMethod]
    public async Task AnalyzeChangeImpactAsync_WhenNodeFound_ReturnsAnalysis()
    {
        // Arrange
        var node = CreateTestNode("node1", "TestClass", "Class");
        var impactAnalysis = new ImpactAnalysis
        {
            SourceNode = node,
            DirectlyAffected = new List<NodeWithDepth>(),
            IndirectlyAffected = new List<NodeWithDepth>()
        };

        mockGraphStore.Setup(x => x.GetNodesAsync(
            It.Is<NodeFilter>(f => f.NamePattern == "TestClass"),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HypergraphNode> { node });

        mockGraphStore.Setup(x => x.AnalyzeImpactAsync(
            "node1",
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(impactAnalysis);

        // Act
        var result = await service.AnalyzeChangeImpactAsync("TestClass");

        // Assert
        result.Should().NotBeNull();
        result!.SourceNode.Name.Should().Be("TestClass");
    }

    [TestMethod]
    public async Task AnalyzeChangeImpactAsync_WhenNodeNotFound_ReturnsNull()
    {
        // Arrange
        mockGraphStore.Setup(x => x.GetNodesAsync(
            It.IsAny<NodeFilter>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HypergraphNode>());

        // Act
        var result = await service.AnalyzeChangeImpactAsync("NonExistent");

        // Assert
        result.Should().BeNull();
    }

    [TestMethod]
    public async Task GetNodesInFileAsync_ReturnsNodesFromFile()
    {
        // Arrange
        var nodes = new List<HypergraphNode>
        {
            CreateTestNode("node1", "Class1", "Class"),
            CreateTestNode("node2", "Method1", "Method")
        };

        mockGraphStore.Setup(x => x.GetNodesAsync(
            It.Is<NodeFilter>(f => f.FilePath == "/test/file.cs"),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(nodes);

        // Act
        var result = await service.GetNodesInFileAsync("/test/file.cs");

        // Assert
        result.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task GetNodesByTypeAsync_ReturnsNodesOfType()
    {
        // Arrange
        var nodes = new List<HypergraphNode>
        {
            CreateTestNode("class1", "Class1", "Class"),
            CreateTestNode("class2", "Class2", "Class")
        };

        mockGraphStore.Setup(x => x.GetNodesAsync(
            It.Is<NodeFilter>(f => f.NodeTypes!.Contains("Class")),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(nodes);

        // Act
        var result = await service.GetNodesByTypeAsync("Class");

        // Assert
        result.Should().HaveCount(2);
    }

    [TestMethod]
    public async Task GetStatisticsAsync_ReturnsStatistics()
    {
        // Arrange
        var stats = new GraphStatistics
        {
            TotalNodes = 100,
            TotalEdges = 50,
            UniqueFiles = 10
        };

        mockGraphStore.Setup(x => x.GetStatisticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(stats);

        // Act
        var result = await service.GetStatisticsAsync();

        // Assert
        result.TotalNodes.Should().Be(100);
        result.TotalEdges.Should().Be(50);
    }

    [TestMethod]
    public async Task EnsureIndexesAsync_CallsGraphStore()
    {
        // Act
        await service.EnsureIndexesAsync();

        // Assert
        mockGraphStore.Verify(x => x.CreateIndexesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ClearAsync_CallsGraphStore()
    {
        // Act
        await service.ClearAsync();

        // Assert
        mockGraphStore.Verify(x => x.ClearAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    private static HypergraphNode CreateTestNode(string id, string name, string nodeType = "Class")
    {
        return new HypergraphNode
        {
            Id = id,
            Name = name,
            NodeType = nodeType,
            Language = "C#",
            FilePath = "/test/file.cs",
            LineStart = 1,
            LineEnd = 10
        };
    }

    private static Hyperedge CreateTestEdge(string id, HyperedgeType edgeType, string[] nodeIds)
    {
        return new Hyperedge
        {
            Id = id,
            EdgeType = edgeType,
            NodeIds = nodeIds.ToList(),
            Roles = new Dictionary<string, string>()
        };
    }
}
