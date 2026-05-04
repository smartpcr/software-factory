// -----------------------------------------------------------------------
// <copyright file="IMemoryGraphStoreContractTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Tests;

using System.Reflection;
using Ast.Core;
using Ast.GraphDb.Interfaces;
using Ast.Memory.Interfaces;
using Ast.Memory.Models;
using Xunit;

/// <summary>
/// Scenario: IMemoryGraphStore contract —
/// Given the IMemoryGraphStore interface,
/// When compiled,
/// Then it extends IGraphStore and declares all 12 methods from architecture.md §5.1.
/// </summary>
public class IMemoryGraphStoreContractTests
{
    private static readonly Type InterfaceType = typeof(IMemoryGraphStore);

    [Fact]
    public void ExtendsIGraphStore()
    {
        Assert.True(typeof(IGraphStore).IsAssignableFrom(InterfaceType),
            "IMemoryGraphStore must extend IGraphStore.");
    }

    [Theory]
    [InlineData("GetNodesByLayerAsync")]
    [InlineData("GetEdgesBySemanticRelationAsync")]
    [InlineData("UpsertNodeAsync")]
    [InlineData("RetireNodeAsync")]
    [InlineData("RetireEdgeAsync")]
    [InlineData("UpdateEdgeMetadataAsync")]
    [InlineData("GetExpertiseForNodeAsync")]
    [InlineData("UpsertExpertiseAsync")]
    [InlineData("ArchiveExpertiseAsync")]
    [InlineData("GetArchivedExpertiseAsync")]
    [InlineData("GetMemoryStatisticsAsync")]
    [InlineData("LogAuditAsync")]
    public void DeclaresRequiredMethod(string methodName)
    {
        var methods = InterfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        Assert.Contains(methods, m => m.Name == methodName);
    }

    [Fact]
    public void DeclaresExactly12Methods()
    {
        // IMemoryGraphStore's own methods (not inherited from IGraphStore/IDisposable)
        var ownMethods = InterfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.Equal(12, ownMethods.Length);
    }

    [Fact]
    public void GetNodesByLayerAsync_ReturnsMemoryNodes()
    {
        var method = InterfaceType.GetMethod("GetNodesByLayerAsync");
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<IReadOnlyList<MemoryNode>>), method!.ReturnType);
    }

    [Fact]
    public void GetEdgesBySemanticRelationAsync_ReturnsMemoryEdges()
    {
        var method = InterfaceType.GetMethod("GetEdgesBySemanticRelationAsync");
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<IReadOnlyList<MemoryEdge>>), method!.ReturnType);
    }

    [Fact]
    public void UpsertNodeAsync_AcceptsHypergraphNodeAndVersionRange()
    {
        var methods = InterfaceType.GetMethods().Where(m => m.Name == "UpsertNodeAsync").ToList();
        // Should have the overload with VersionRange (declared on IMemoryGraphStore)
        var versionedOverload = methods.FirstOrDefault(m =>
        {
            var parameters = m.GetParameters();
            return parameters.Any(p => p.ParameterType == typeof(VersionRange));
        });
        Assert.NotNull(versionedOverload);
        var parms = versionedOverload!.GetParameters();
        Assert.Equal(typeof(HypergraphNode), parms[0].ParameterType);
        Assert.Equal(typeof(VersionRange), parms[1].ParameterType);
        Assert.Equal(typeof(CancellationToken), parms[2].ParameterType);
    }

    [Fact]
    public void RetireNodeAsync_AcceptsNodeIdAndCommitIndex()
    {
        var method = InterfaceType.GetMethod("RetireNodeAsync");
        Assert.NotNull(method);
        var parms = method!.GetParameters();
        Assert.Equal(typeof(string), parms[0].ParameterType);
        Assert.Equal(typeof(int), parms[1].ParameterType);
    }

    [Fact]
    public void RetireEdgeAsync_AcceptsEdgeIdAndCommitIndex()
    {
        var method = InterfaceType.GetMethod("RetireEdgeAsync");
        Assert.NotNull(method);
        var parms = method!.GetParameters();
        Assert.Equal(typeof(string), parms[0].ParameterType);
        Assert.Equal(typeof(int), parms[1].ParameterType);
    }

    [Fact]
    public void UpdateEdgeMetadataAsync_AcceptsMetadataDictionary()
    {
        var method = InterfaceType.GetMethod("UpdateEdgeMetadataAsync");
        Assert.NotNull(method);
        var parms = method!.GetParameters();
        Assert.Equal(typeof(string), parms[0].ParameterType);
        Assert.Equal(typeof(IDictionary<string, object?>), parms[1].ParameterType);
    }

    [Fact]
    public void GetExpertiseForNodeAsync_ReturnsExpertiseEntries()
    {
        var method = InterfaceType.GetMethod("GetExpertiseForNodeAsync");
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<IReadOnlyList<ExpertiseEntry>>), method!.ReturnType);
    }

    [Fact]
    public void UpsertExpertiseAsync_AcceptsExpertiseEntry()
    {
        var method = InterfaceType.GetMethod("UpsertExpertiseAsync");
        Assert.NotNull(method);
        Assert.Equal(typeof(ExpertiseEntry), method!.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void GetMemoryStatisticsAsync_ReturnsStatistics()
    {
        var method = InterfaceType.GetMethod("GetMemoryStatisticsAsync");
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<MemoryGraphStatistics>), method!.ReturnType);
    }

    [Fact]
    public void LogAuditAsync_AcceptsSessionIdOperationAndDetail()
    {
        var method = InterfaceType.GetMethod("LogAuditAsync");
        Assert.NotNull(method);
        var parms = method!.GetParameters();
        Assert.Equal(typeof(string), parms[0].ParameterType);   // sessionId
        Assert.Equal(typeof(string), parms[1].ParameterType);   // operation
        Assert.Equal(typeof(object), parms[2].ParameterType);   // detail
        Assert.Equal(typeof(CancellationToken), parms[3].ParameterType);
    }
}