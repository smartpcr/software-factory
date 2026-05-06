// -----------------------------------------------------------------------
// <copyright file="LouvainCommunityDetector.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Evolution;

/// <summary>
/// Simple Louvain modularity-based community detection.
/// Operates on an undirected adjacency list and produces community
/// assignments that maximize modularity.
/// </summary>
internal sealed class LouvainCommunityDetector
{
    /// <summary>
    /// Runs Louvain community detection on the given adjacency list.
    /// Returns a dictionary mapping each node to its community ID.
    /// </summary>
    public static Dictionary<string, int> Detect(Dictionary<string, HashSet<string>> adjacency)
    {
        if (adjacency.Count == 0)
        {
            return [];
        }

        var nodes = adjacency.Keys.ToList();
        var communityOf = new Dictionary<string, int>();
        var communityId = 0;
        foreach (var node in nodes)
        {
            communityOf[node] = communityId++;
        }

        // Total edges (each undirected edge counted once in m)
        var totalEdgeWeight = 0.0;
        foreach (var kvp in adjacency)
        {
            totalEdgeWeight += kvp.Value.Count;
        }

        totalEdgeWeight /= 2.0; // each edge counted twice in adjacency
        if (totalEdgeWeight == 0)
        {
            return communityOf;
        }

        // Degree of each node
        var degree = new Dictionary<string, double>();
        foreach (var kvp in adjacency)
        {
            degree[kvp.Key] = kvp.Value.Count;
        }

        // Sum of degrees in each community
        var communityDegreeSum = new Dictionary<int, double>();
        foreach (var node in nodes)
        {
            var c = communityOf[node];
            communityDegreeSum.TryAdd(c, 0.0);
            communityDegreeSum[c] += degree[node];
        }

        // Internal edges in each community
        var communityInternalEdges = new Dictionary<int, double>();
        foreach (var node in nodes)
        {
            var c = communityOf[node];
            communityInternalEdges.TryAdd(c, 0.0);
            foreach (var neighbor in adjacency[node])
            {
                if (communityOf.TryGetValue(neighbor, out var nc) && nc == c)
                {
                    communityInternalEdges[c] += 0.5; // counted from both sides
                }
            }
        }

        // Phase 1: iterative node moves to maximize modularity
        var improved = true;
        var maxIterations = 100;
        while (improved && maxIterations-- > 0)
        {
            improved = false;
            foreach (var node in nodes)
            {
                var currentCommunity = communityOf[node];
                var nodeDeg = degree[node];

                // Count edges to each neighbor community
                var neighborCommunityEdges = new Dictionary<int, double>();
                foreach (var neighbor in adjacency[node])
                {
                    if (!communityOf.TryGetValue(neighbor, out var nc))
                    {
                        continue;
                    }

                    neighborCommunityEdges.TryAdd(nc, 0.0);
                    neighborCommunityEdges[nc] += 1.0;
                }

                // Edges to current community (excluding self-loops)
                var edgesToCurrent = neighborCommunityEdges.GetValueOrDefault(currentCommunity, 0.0);

                // Remove node from its community
                communityDegreeSum[currentCommunity] -= nodeDeg;
                communityInternalEdges.TryAdd(currentCommunity, 0.0);
                communityInternalEdges[currentCommunity] -= edgesToCurrent;

                // Find best community
                var bestCommunity = currentCommunity;
                var bestGain = 0.0;

                foreach (var (candidateCommunity, edgesToCandidate) in neighborCommunityEdges)
                {
                    var sigmaTot = communityDegreeSum.GetValueOrDefault(candidateCommunity, 0.0);
                    var gain = (edgesToCandidate / totalEdgeWeight)
                             - (sigmaTot * nodeDeg / (2.0 * totalEdgeWeight * totalEdgeWeight));
                    if (gain > bestGain)
                    {
                        bestGain = gain;
                        bestCommunity = candidateCommunity;
                    }
                }

                // Move node to best community
                communityOf[node] = bestCommunity;
                communityDegreeSum.TryAdd(bestCommunity, 0.0);
                communityDegreeSum[bestCommunity] += nodeDeg;
                var edgesToBest = neighborCommunityEdges.GetValueOrDefault(bestCommunity, 0.0);
                communityInternalEdges.TryAdd(bestCommunity, 0.0);
                communityInternalEdges[bestCommunity] += edgesToBest;

                if (bestCommunity != currentCommunity)
                {
                    improved = true;
                }
            }
        }

        // Re-number communities contiguously
        var renumber = new Dictionary<int, int>();
        var nextId = 0;
        foreach (var node in nodes)
        {
            var c = communityOf[node];
            if (!renumber.ContainsKey(c))
            {
                renumber[c] = nextId++;
            }

            communityOf[node] = renumber[c];
        }

        return communityOf;
    }
}