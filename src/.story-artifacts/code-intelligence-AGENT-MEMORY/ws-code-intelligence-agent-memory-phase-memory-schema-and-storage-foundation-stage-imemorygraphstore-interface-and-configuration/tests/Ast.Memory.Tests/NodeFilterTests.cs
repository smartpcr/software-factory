// -----------------------------------------------------------------------
// <copyright file="NodeFilterTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Tests;

using Ast.Memory.Models;
using Xunit;

public class NodeFilterTests
{
    [Fact]
    public void Defaults_AreAllNull()
    {
        var filter = new NodeFilter();
        Assert.Null(filter.NodeTypes);
        Assert.Null(filter.ContentHash);
        Assert.Null(filter.QualifiedNameContains);
        Assert.Null(filter.MetadataMatches);
    }

    [Fact]
    public void CanSetAllProperties()
    {
        var filter = new NodeFilter
        {
            NodeTypes = new[] { "Class", "Function" },
            ContentHash = "abc123",
            QualifiedNameContains = "MyService",
            MetadataMatches = new Dictionary<string, string> { ["Package"] = "core" }
        };

        Assert.Equal(2, filter.NodeTypes!.Count);
        Assert.Equal("abc123", filter.ContentHash);
        Assert.Equal("MyService", filter.QualifiedNameContains);
        Assert.Single(filter.MetadataMatches!);
    }
}