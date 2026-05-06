// -----------------------------------------------------------------------
// <copyright file="LouvainCommunityDetectorTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.UnitTests;

using Ast.Memory.Evolution;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Unit tests for the pure Louvain community detection algorithm.
/// </summary>
[TestClass]
public class LouvainCommunityDetectorTests
{
    [TestMethod]
    public void Detect_EmptyGraph_ReturnsEmpty()
    {
        var detector = new LouvainCommunityDetector();
        var result = detector.Detect([], []);
        result.Should().BeEmpty();
    }

    [TestMethod]
    public void Detect_SingleNode_ReturnsSingleCommunity()
    {
        var detector = new LouvainCommunityDetector();
        var result = detector.Detect(["a"], []);
        result.Should().HaveCount(1);
        result.Should().ContainKey("a");
    }

    [TestMethod]
    public void Detect_TwoClusters_AssignsDifferentCommunities()
    {
        var detector = new LouvainCommunityDetector();
        var nodes = new List<string> { "a1", "a2", "a3", "b1", "b2", "b3" };
        var edges = new List<(string, string, double)>
        {
            // Cluster A: dense
            ("a1", "a2", 5.0),
            ("a2", "a3", 5.0),
            ("a1", "a3", 5.0),
            // Cluster B: dense
            ("b1", "b2", 5.0),
            ("b2", "b3", 5.0),
            ("b1", "b3", 5.0),
            // Weak inter-cluster link
            ("a1", "b1", 0.1)
        };

        var result = detector.Detect(nodes, edges);

        // All A-nodes should be in the same community
        result["a1"].Should().Be(result["a2"]);
        result["a2"].Should().Be(result["a3"]);

        // All B-nodes should be in the same community
        result["b1"].Should().Be(result["b2"]);
        result["b2"].Should().Be(result["b3"]);

        // A and B should be in different communities
        result["a1"].Should().NotBe(result["b1"]);
    }

    [TestMethod]
    public void Detect_ThreeClusters_AssignsThreeCommunities()
    {
        var detector = new LouvainCommunityDetector();
        var nodes = new List<string>();
        var edges = new List<(string, string, double)>();

        // Create 3 dense clusters of 5 nodes each
        for (var cluster = 0; cluster < 3; cluster++)
        {
            var prefix = $"c{cluster}";
            for (var i = 0; i < 5; i++)
            {
                nodes.Add($"{prefix}-{i}");
            }

            // Dense intra-cluster edges
            for (var i = 0; i < 5; i++)
            {
                for (var j = i + 1; j < 5; j++)
                {
                    edges.Add(($"{prefix}-{i}", $"{prefix}-{j}", 5.0));
                }
            }
        }

        // Weak inter-cluster edges
        edges.Add(("c0-0", "c1-0", 0.1));
        edges.Add(("c1-0", "c2-0", 0.1));

        var result = detector.Detect(nodes, edges);

        // Count distinct communities
        var communityCount = result.Values.Distinct().Count();
        communityCount.Should().BeGreaterThanOrEqualTo(3,
            "three dense clusters with weak inter-cluster links should be detected");
    }
}