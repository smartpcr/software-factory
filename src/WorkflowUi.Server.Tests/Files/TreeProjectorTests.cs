// -----------------------------------------------------------------------
// <copyright file="TreeProjectorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace WorkflowUi.Server.Tests.Files;

using System;
using System.Collections.Generic;
using System.Linq;
using WorkflowUi.Server.Files;
using WorkflowUi.Server.Persistence;
using Xunit;

public class TreeProjectorTests
{
    [Fact]
    public void FlatIds_ProducesFlatTree()
    {
        var summaries = new List<WorkflowSummary>
        {
            new("wf1", DateTime.UtcNow, 100),
            new("wf2", DateTime.UtcNow, 200),
        };

        var tree = TreeProjector.Project(summaries);

        Assert.Equal(2, tree.Children.Count);
        Assert.All(tree.Children, c => Assert.False(c.IsFolder));
    }

    [Fact]
    public void SlashSeparated_ProducesNestedTree()
    {
        var summaries = new List<WorkflowSummary>
        {
            new("marketing/newsletter", DateTime.UtcNow, 100),
            new("marketing/campaign", DateTime.UtcNow, 200),
            new("ops/deploy", DateTime.UtcNow, 300),
        };

        var tree = TreeProjector.Project(summaries);

        Assert.Equal(2, tree.Children.Count);
        var marketing = tree.Children.Single(c => c.Name == "marketing");
        Assert.True(marketing.IsFolder);
        Assert.Equal(2, marketing.Children.Count);
        var ops = tree.Children.Single(c => c.Name == "ops");
        Assert.True(ops.IsFolder);
        Assert.Single(ops.Children);
    }

    [Fact]
    public void DeepNesting_PreservesOrder()
    {
        var summaries = new List<WorkflowSummary>
        {
            new("a/b/c/deep", DateTime.UtcNow, 100),
        };

        var tree = TreeProjector.Project(summaries);

        var a = tree.Children.Single();
        Assert.Equal("a", a.Name);
        Assert.True(a.IsFolder);
        var b = a.Children.Single();
        Assert.Equal("b", b.Name);
        var c = b.Children.Single();
        Assert.Equal("c", c.Name);
        var leaf = c.Children.Single();
        Assert.Equal("deep", leaf.Name);
        Assert.False(leaf.IsFolder);
    }

    [Fact]
    public void EmptyList_ReturnsEmptyRoot()
    {
        var tree = TreeProjector.Project(new List<WorkflowSummary>());
        Assert.Empty(tree.Children);
    }

    [Fact]
    public void Leaf_Contains_WorkflowId()
    {
        var summaries = new List<WorkflowSummary>
        {
            new("marketing/newsletter", DateTime.UtcNow, 100),
        };

        var tree = TreeProjector.Project(summaries);
        var leaf = tree.Children.Single().Children.Single();
        Assert.Equal("marketing/newsletter", leaf.WorkflowId);
    }

    [Fact]
    public void Folder_Has_No_WorkflowId()
    {
        var summaries = new List<WorkflowSummary>
        {
            new("marketing/newsletter", DateTime.UtcNow, 100),
        };

        var tree = TreeProjector.Project(summaries);
        var folder = tree.Children.Single();
        Assert.Null(folder.WorkflowId);
    }
}
