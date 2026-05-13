// -----------------------------------------------------------------------
// <copyright file="LouvainCommunityDetector.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Memory.Evolution;

/// <summary>
/// Full multilevel Louvain method for community detection on weighted undirected graphs.
/// Phase 1: Local moves to maximize modularity gain.
/// Phase 2: Aggregate communities into super-nodes, preserving intra-community
///          weight as self-loops.
/// Repeat until modularity no longer improves.
/// Reference: Blondel et al., "Fast unfolding of communities in large networks" (2008).
/// </summary>
internal sealed class LouvainCommunityDetector
{
    /// <summary>
    /// Detects communities in the graph defined by the given edges.
    /// Returns a mapping from node ID to community (cluster) index.
    /// </summary>
    public Dictionary<string, int> Detect(
        IReadOnlyList<string> nodeIds,
        IReadOnlyList<(string Source, string Target, double Weight)> edges)
    {
        if (nodeIds.Count == 0)
        {
            return new Dictionary<string, int>();
        }

        // Map string IDs to integer indices
        var idToIndex = new Dictionary<string, int>(nodeIds.Count);
        var indexToId = new string[nodeIds.Count];
        for (var i = 0; i < nodeIds.Count; i++)
        {
            idToIndex[nodeIds[i]] = i;
            indexToId[i] = nodeIds[i];
        }

        var n = nodeIds.Count;

        // Build adjacency with weights (undirected: both directions)
        var adjacency = BuildAdjacency(n, edges, idToIndex);
        var selfLoops = new double[n]; // initially zero

        // Compute total edge weight (m)
        var m = ComputeTotalWeight(n, adjacency, selfLoops);

        if (m == 0.0)
        {
            // No edges: each node is its own community
            var isolated = new Dictionary<string, int>(n);
            for (var i = 0; i < n; i++)
            {
                isolated[indexToId[i]] = i;
            }

            return isolated;
        }

        // Initial community assignment: each node in its own community
        var community = new int[n];
        for (var i = 0; i < n; i++)
        {
            community[i] = i;
        }

        // Multilevel Louvain loop
        var currentN = n;
        var currentAdj = adjacency;
        var currentSelfLoops = selfLoops;
        var currentCommunity = community;
        var currentM = m;

        // Track hierarchical community assignments for final unpacking
        var hierarchyMaps = new List<int[]>();

        while (true)
        {
            // Phase 1: Local moves
            var moved = LocalMovePhase(
                currentN, currentAdj, currentSelfLoops, currentCommunity, currentM);

            if (!moved)
            {
                break; // No improvement — stop
            }

            // Store this level's renumbered assignment for later unpacking.
            // Map each node to its super-node index (not raw community ID).
            var (superN, superAdj, superSelfLoops, communityMapping) =
                AggregateGraph(currentN, currentAdj, currentSelfLoops, currentCommunity);

            if (superN >= currentN)
            {
                break; // No further aggregation possible
            }

            // Build renumbered map: node i → super-node index
            var renumbered = new int[currentN];
            for (var i = 0; i < currentN; i++)
            {
                renumbered[i] = communityMapping[currentCommunity[i]];
            }

            hierarchyMaps.Add(renumbered);

            // Prepare next level
            currentN = superN;
            currentAdj = superAdj;
            currentSelfLoops = superSelfLoops;
            currentCommunity = new int[superN];
            for (var i = 0; i < superN; i++)
            {
                currentCommunity[i] = i;
            }

            currentM = ComputeTotalWeight(superN, superAdj, superSelfLoops);
        }

        // Unpack hierarchical assignments back to original nodes
        var finalCommunity = new int[n];
        for (var i = 0; i < n; i++)
        {
            finalCommunity[i] = i;
        }

        foreach (var levelMap in hierarchyMaps)
        {
            for (var i = 0; i < n; i++)
            {
                finalCommunity[i] = levelMap[finalCommunity[i]];
            }
        }

        // Renumber communities to contiguous indices starting from 0
        var communityRenumber = new Dictionary<int, int>();
        var nextCommunityId = 0;
        var result = new Dictionary<string, int>(n);

        for (var i = 0; i < n; i++)
        {
            var c = finalCommunity[i];
            if (!communityRenumber.TryGetValue(c, out var mapped))
            {
                mapped = nextCommunityId++;
                communityRenumber[c] = mapped;
            }

            result[indexToId[i]] = mapped;
        }

        return result;
    }

    /// <summary>
    /// Phase 1: Iteratively move nodes to neighboring communities to maximize
    /// modularity gain. Returns true if any node was moved.
    /// </summary>
    private static bool LocalMovePhase(
        int n,
        Dictionary<int, List<(int Neighbor, double Weight)>> adjacency,
        double[] selfLoops,
        int[] community,
        double m)
    {
        // Compute degree of each node (ki = sum of adjacent edge weights + 2*self-loop)
        var degree = new double[n];
        for (var i = 0; i < n; i++)
        {
            degree[i] = 2.0 * selfLoops[i];
            foreach (var (_, w) in adjacency[i])
            {
                degree[i] += w;
            }
        }

        // Sum of degrees for each community (Σ_tot)
        var communityDegreeSum = new Dictionary<int, double>(n);
        for (var i = 0; i < n; i++)
        {
            if (!communityDegreeSum.ContainsKey(community[i]))
            {
                communityDegreeSum[community[i]] = 0.0;
            }

            communityDegreeSum[community[i]] += degree[i];
        }

        var anyMoved = false;
        var improved = true;
        var maxIterations = 100;
        var iteration = 0;
        var twoM = 2.0 * m;

        while (improved && iteration < maxIterations)
        {
            improved = false;
            iteration++;

            for (var node = 0; node < n; node++)
            {
                var currentCommunity = community[node];
                var ki = degree[node];
                var bestCommunity = currentCommunity;
                var bestNetGain = 0.0;

                // Compute edge weight from this node to each neighboring community
                var weightToCommunity = new Dictionary<int, double>();
                foreach (var (neighbor, w) in adjacency[node])
                {
                    var nc = community[neighbor];
                    if (!weightToCommunity.ContainsKey(nc))
                    {
                        weightToCommunity[nc] = 0.0;
                    }

                    weightToCommunity[nc] += w;
                }

                // Cost of removing node from its current community
                weightToCommunity.TryGetValue(currentCommunity, out var kiInCurrent);
                var sigmaTotCurrent = communityDegreeSum[currentCommunity] - ki;
                var removeCost = kiInCurrent / m - (sigmaTotCurrent * ki) / (twoM * m);

                // Try each neighboring community
                foreach (var (candidateCommunity, kiInCandidate) in weightToCommunity)
                {
                    if (candidateCommunity == currentCommunity)
                    {
                        continue;
                    }

                    var sigmaTotCandidate = communityDegreeSum[candidateCommunity];
                    var addGain = kiInCandidate / m - (sigmaTotCandidate * ki) / (twoM * m);

                    var netGain = addGain - removeCost;
                    if (netGain > bestNetGain)
                    {
                        bestNetGain = netGain;
                        bestCommunity = candidateCommunity;
                    }
                }

                if (bestCommunity != currentCommunity)
                {
                    communityDegreeSum[currentCommunity] -= ki;
                    if (communityDegreeSum[currentCommunity] < 1e-12)
                    {
                        communityDegreeSum.Remove(currentCommunity);
                    }

                    community[node] = bestCommunity;
                    if (!communityDegreeSum.ContainsKey(bestCommunity))
                    {
                        communityDegreeSum[bestCommunity] = 0.0;
                    }

                    communityDegreeSum[bestCommunity] += ki;
                    improved = true;
                    anyMoved = true;
                }
            }
        }

        return anyMoved;
    }

    /// <summary>
    /// Phase 2: Aggregate communities into super-nodes.
    /// Intra-community edge weights become self-loops on the super-node.
    /// Inter-community edge weights become weighted edges between super-nodes.
    /// </summary>
    private static (
        int SuperN,
        Dictionary<int, List<(int Neighbor, double Weight)>> SuperAdj,
        double[] SuperSelfLoops,
        Dictionary<int, int> CommunityMapping) AggregateGraph(
        int n,
        Dictionary<int, List<(int Neighbor, double Weight)>> adjacency,
        double[] selfLoops,
        int[] community)
    {
        // Renumber communities to contiguous range
        var communityMapping = new Dictionary<int, int>();
        var nextId = 0;
        for (var i = 0; i < n; i++)
        {
            if (!communityMapping.ContainsKey(community[i]))
            {
                communityMapping[community[i]] = nextId++;
            }
        }

        var superN = nextId;
        var superSelfLoops = new double[superN];
        var superAdj = new Dictionary<int, List<(int, double)>>(superN);
        for (var i = 0; i < superN; i++)
        {
            superAdj[i] = new List<(int, double)>();
        }

        // Accumulate self-loops from original nodes into their super-node
        for (var i = 0; i < n; i++)
        {
            var superI = communityMapping[community[i]];
            superSelfLoops[superI] += selfLoops[i];
        }

        // Aggregate edges: intra-community → self-loop, inter-community → super-edge
        // Use a temporary accumulator to merge parallel edges
        var edgeAccum = new Dictionary<(int, int), double>();

        for (var i = 0; i < n; i++)
        {
            var superI = communityMapping[community[i]];
            foreach (var (j, w) in adjacency[i])
            {
                var superJ = communityMapping[community[j]];
                if (superI == superJ)
                {
                    // Intra-community edge becomes self-loop
                    // Each undirected edge (i,j) appears twice; divide by 2
                    superSelfLoops[superI] += w / 2.0;
                }
                else
                {
                    // Inter-community edge — accumulate (direction-normalized)
                    var key = superI < superJ ? (superI, superJ) : (superJ, superI);
                    if (!edgeAccum.ContainsKey(key))
                    {
                        edgeAccum[key] = 0.0;
                    }

                    // Each undirected edge appears twice in adjacency; accumulate half
                    edgeAccum[key] += w / 2.0;
                }
            }
        }

        // Build super-adjacency (undirected: add both directions)
        foreach (var ((a, b), w) in edgeAccum)
        {
            superAdj[a].Add((b, w));
            superAdj[b].Add((a, w));
        }

        return (superN, superAdj, superSelfLoops, communityMapping);
    }

    private static Dictionary<int, List<(int Neighbor, double Weight)>> BuildAdjacency(
        int n,
        IReadOnlyList<(string Source, string Target, double Weight)> edges,
        Dictionary<string, int> idToIndex)
    {
        var adjacency = new Dictionary<int, List<(int, double)>>(n);
        for (var i = 0; i < n; i++)
        {
            adjacency[i] = new List<(int, double)>();
        }

        foreach (var (source, target, weight) in edges)
        {
            if (!idToIndex.TryGetValue(source, out var si) ||
                !idToIndex.TryGetValue(target, out var ti))
            {
                continue;
            }

            if (si == ti)
            {
                continue; // skip self-loops in input
            }

            adjacency[si].Add((ti, weight));
            adjacency[ti].Add((si, weight));
        }

        return adjacency;
    }

    private static double ComputeTotalWeight(
        int n,
        Dictionary<int, List<(int Neighbor, double Weight)>> adjacency,
        double[] selfLoops)
    {
        var m = 0.0;

        // Self-loops contribute their weight to total m
        for (var i = 0; i < n; i++)
        {
            m += selfLoops[i];
        }

        // Each undirected edge appears twice in adjacency; count once
        for (var i = 0; i < n; i++)
        {
            foreach (var (_, w) in adjacency[i])
            {
                m += w;
            }
        }

        // adjacency already has both directions, so total = Σ(adj) / 2 + selfLoops
        // But we double-counted above: m = selfLoops + Σ(adj)
        // For modularity, m = sum of all unique edge weights + self-loops
        // Σ(adj) counts each undirected edge twice, so unique = Σ(adj)/2
        m = (m - selfLoops.Sum()) / 2.0 + selfLoops.Sum();

        return m;
    }
}