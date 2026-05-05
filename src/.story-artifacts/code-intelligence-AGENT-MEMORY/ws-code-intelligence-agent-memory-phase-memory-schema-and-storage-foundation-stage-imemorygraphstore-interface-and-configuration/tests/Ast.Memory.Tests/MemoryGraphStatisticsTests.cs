// -----------------------------------------------------------------------
// <copyright file="MemoryGraphStatisticsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Tests;

using Ast.Memory.Models;
using Xunit;

public class MemoryGraphStatisticsTests
{
    [Fact]
    public void DefaultValues_AreZero()
    {
        var stats = new MemoryGraphStatistics();
        Assert.Equal(0, stats.TotalNodes);
        Assert.Equal(0, stats.TotalEdges);
        Assert.Equal(0, stats.TotalExpertiseEntries);
        Assert.Equal(0, stats.ArchivedExpertiseEntries);
        Assert.Empty(stats.ByLayer);
    }

    [Fact]
    public void ByLayer_CanBePopulated()
    {
        var stats = new MemoryGraphStatistics
        {
            TotalNodes = 150,
            TotalEdges = 300,
            TotalExpertiseEntries = 20,
            ArchivedExpertiseEntries = 5,
            ByLayer = new Dictionary<string, LayerStatistics>
            {
                ["structural"] = new LayerStatistics { NodeCount = 100, EdgeCount = 200 },
                ["semantic"] = new LayerStatistics { NodeCount = 50, EdgeCount = 100 }
            }
        };

        Assert.Equal(150, stats.TotalNodes);
        Assert.Equal(2, stats.ByLayer.Count);
        Assert.Equal(100, stats.ByLayer["structural"].NodeCount);
        Assert.Equal(200, stats.ByLayer["structural"].EdgeCount);
        Assert.Equal(50, stats.ByLayer["semantic"].NodeCount);
    }
}