// -----------------------------------------------------------------------
// <copyright file="IMemoryGraphStoreContractTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Tests;

using System.Reflection;
using Ast.Core;
using Ast.GraphDb;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;

[TestClass]
public class IMemoryGraphStoreContractTests
{
    private static readonly Type InterfaceType = typeof(IMemoryGraphStore);

    [TestMethod]
    public void ExtendsIGraphStore()
    {
        typeof(IGraphStore).IsAssignableFrom(InterfaceType).Should().BeTrue(
            "IMemoryGraphStore must extend IGraphStore");
    }

    [TestMethod]
    public void ExtendsIAsyncDisposable()
    {
        typeof(IAsyncDisposable).IsAssignableFrom(InterfaceType).Should().BeTrue(
            "IMemoryGraphStore must be IAsyncDisposable via IGraphStore");
    }

    [TestMethod]
    public void DeclaresExactly12Methods()
    {
        var methods = InterfaceType.GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        methods.Should().HaveCount(12,
            "architecture.md §5.1 defines exactly 12 methods");
    }

    [TestMethod]
    public void GetNodesByLayerAsync_Signature()
    {
        var m = GetMethod(nameof(IMemoryGraphStore.GetNodesByLayerAsync));
        m.ReturnType.Should().Be(typeof(Task<IReadOnlyList<MemoryNode>>));
        var p = m.GetParameters();
        p.Should().HaveCount(5);
        p[0].ParameterType.Should().Be(typeof(string));            // memoryLayer
        p[1].ParameterType.Should().Be(typeof(int?));              // atCommitIndex
        p[2].ParameterType.Should().Be(typeof(NodeFilter));        // filter (nullable ref)
        p[3].ParameterType.Should().Be(typeof(int));               // limit
        p[4].ParameterType.Should().Be(typeof(CancellationToken)); // cancellationToken
    }

    [TestMethod]
    public void GetEdgesBySemanticRelationAsync_Signature()
    {
        var m = GetMethod(nameof(IMemoryGraphStore.GetEdgesBySemanticRelationAsync));
        m.ReturnType.Should().Be(typeof(Task<IReadOnlyList<MemoryEdge>>));
        var p = m.GetParameters();
        p.Should().HaveCount(5);
        p[0].ParameterType.Should().Be(typeof(string));            // semanticRelation
        p[1].ParameterType.Should().Be(typeof(string));            // memoryLayer (nullable ref)
        p[2].ParameterType.Should().Be(typeof(int?));              // atCommitIndex
        p[3].ParameterType.Should().Be(typeof(int));               // limit
        p[4].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [TestMethod]
    public void UpsertNodeAsync_Signature()
    {
        var m = GetMethod(nameof(IMemoryGraphStore.UpsertNodeAsync));
        m.ReturnType.Should().Be(typeof(Task));
        var p = m.GetParameters();
        p.Should().HaveCount(3);
        p[0].ParameterType.Should().Be(typeof(HypergraphNode));
        p[1].ParameterType.Should().Be(typeof(VersionRange));
        p[2].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [TestMethod]
    public void RetireNodeAsync_Signature()
    {
        var m = GetMethod(nameof(IMemoryGraphStore.RetireNodeAsync));
        m.ReturnType.Should().Be(typeof(Task));
        var p = m.GetParameters();
        p.Should().HaveCount(3);
        p[0].ParameterType.Should().Be(typeof(string));
        p[1].ParameterType.Should().Be(typeof(int));
        p[2].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [TestMethod]
    public void RetireEdgeAsync_Signature()
    {
        var m = GetMethod(nameof(IMemoryGraphStore.RetireEdgeAsync));
        m.ReturnType.Should().Be(typeof(Task));
        var p = m.GetParameters();
        p.Should().HaveCount(3);
        p[0].ParameterType.Should().Be(typeof(string));
        p[1].ParameterType.Should().Be(typeof(int));
        p[2].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [TestMethod]
    public void UpdateEdgeMetadataAsync_Signature()
    {
        var m = GetMethod(nameof(IMemoryGraphStore.UpdateEdgeMetadataAsync));
        m.ReturnType.Should().Be(typeof(Task));
        var p = m.GetParameters();
        p.Should().HaveCount(3);
        p[0].ParameterType.Should().Be(typeof(string));
        p[1].ParameterType.Should().Be(typeof(IDictionary<string, object?>));
        p[2].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [TestMethod]
    public void GetExpertiseForNodeAsync_Signature()
    {
        var m = GetMethod(nameof(IMemoryGraphStore.GetExpertiseForNodeAsync));
        m.ReturnType.Should().Be(typeof(Task<IReadOnlyList<ExpertiseEntry>>));
        var p = m.GetParameters();
        p.Should().HaveCount(3);
        p[0].ParameterType.Should().Be(typeof(string));
        p[1].ParameterType.Should().Be(typeof(double));
        p[2].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [TestMethod]
    public void UpsertExpertiseAsync_Signature()
    {
        var m = GetMethod(nameof(IMemoryGraphStore.UpsertExpertiseAsync));
        m.ReturnType.Should().Be(typeof(Task));
        var p = m.GetParameters();
        p.Should().HaveCount(2);
        p[0].ParameterType.Should().Be(typeof(ExpertiseEntry));
        p[1].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [TestMethod]
    public void ArchiveExpertiseAsync_Signature()
    {
        var m = GetMethod(nameof(IMemoryGraphStore.ArchiveExpertiseAsync));
        m.ReturnType.Should().Be(typeof(Task));
        var p = m.GetParameters();
        p.Should().HaveCount(2);
        p[0].ParameterType.Should().Be(typeof(string));
        p[1].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [TestMethod]
    public void GetArchivedExpertiseAsync_Signature()
    {
        var m = GetMethod(nameof(IMemoryGraphStore.GetArchivedExpertiseAsync));
        m.ReturnType.Should().Be(typeof(Task<IReadOnlyList<ExpertiseEntry>>));
        var p = m.GetParameters();
        p.Should().HaveCount(3);
        p[0].ParameterType.Should().Be(typeof(string));  // nullable ref
        p[1].ParameterType.Should().Be(typeof(int));
        p[2].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [TestMethod]
    public void GetMemoryStatisticsAsync_Signature()
    {
        var m = GetMethod(nameof(IMemoryGraphStore.GetMemoryStatisticsAsync));
        m.ReturnType.Should().Be(typeof(Task<MemoryGraphStatistics>));
        var p = m.GetParameters();
        p.Should().HaveCount(1);
        p[0].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [TestMethod]
    public void LogAuditAsync_Signature()
    {
        var m = GetMethod(nameof(IMemoryGraphStore.LogAuditAsync));
        m.ReturnType.Should().Be(typeof(Task));
        var p = m.GetParameters();
        p.Should().HaveCount(4);
        p[0].ParameterType.Should().Be(typeof(string));
        p[1].ParameterType.Should().Be(typeof(string));
        p[2].ParameterType.Should().Be(typeof(object));
        p[3].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [TestMethod]
    public void GetNodesByLayerAsync_OptionalParametersAreNullable()
    {
        var m = GetMethod(nameof(IMemoryGraphStore.GetNodesByLayerAsync));
        var p = m.GetParameters();

        // atCommitIndex (int?) is a Nullable<int> value type
        p[1].ParameterType.Should().Be(typeof(int?), "atCommitIndex should be nullable");

        // filter (NodeFilter?) is a nullable reference type — NullabilityInfoContext surfaces this
        var ctx = new NullabilityInfoContext();
        var filterNullability = ctx.Create(p[2]);
        filterNullability.ReadState.Should().Be(NullabilityState.Nullable,
            "filter parameter should be annotated as nullable");
    }

    [TestMethod]
    public void GetEdgesBySemanticRelationAsync_OptionalParametersAreNullable()
    {
        var m = GetMethod(nameof(IMemoryGraphStore.GetEdgesBySemanticRelationAsync));
        var p = m.GetParameters();
        var ctx = new NullabilityInfoContext();

        // memoryLayer (string?) should be nullable
        var layerNullability = ctx.Create(p[1]);
        layerNullability.ReadState.Should().Be(NullabilityState.Nullable,
            "memoryLayer parameter should be annotated as nullable");

        // atCommitIndex (int?) should be nullable
        p[2].ParameterType.Should().Be(typeof(int?), "atCommitIndex should be nullable");
    }

    [TestMethod]
    public void GetArchivedExpertiseAsync_TargetNodeIdIsNullable()
    {
        var m = GetMethod(nameof(IMemoryGraphStore.GetArchivedExpertiseAsync));
        var p = m.GetParameters();
        var ctx = new NullabilityInfoContext();

        var nullability = ctx.Create(p[0]);
        nullability.ReadState.Should().Be(NullabilityState.Nullable,
            "targetNodeId parameter should be annotated as nullable");
    }

    [TestMethod]
    public void MemoryOptions_NullableBackendEndpoints()
    {
        var opts = new MemoryOptions();
        opts.SqlitePath.Should().BeNull("SqlitePath has no default");
        opts.Neo4jUri.Should().BeNull("Neo4jUri has no default");
        opts.QdrantEndpoint.Should().BeNull("QdrantEndpoint has no default");
    }

    private static MethodInfo GetMethod(string name)
    {
        var method = InterfaceType.GetMethod(name,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        method.Should().NotBeNull($"interface must declare method '{name}'");
        return method!;
    }
}
