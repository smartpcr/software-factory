// Social Network Analysis Algorithms for NVL Graph

import type { GraphNode, GraphEdge, SNAMetrics } from '../types';

interface AdjacencyList {
  outgoing: Map<string, string[]>;
  incoming: Map<string, string[]>;
  all: Map<string, string[]>;
}

// Build adjacency lists from nodes and edges
function buildAdjacencyList(
  nodes: GraphNode[],
  edges: GraphEdge[]
): AdjacencyList {
  const outgoing = new Map<string, string[]>();
  const incoming = new Map<string, string[]>();
  const all = new Map<string, string[]>();

  nodes.forEach((node) => {
    outgoing.set(node.id, []);
    incoming.set(node.id, []);
    all.set(node.id, []);
  });

  edges.forEach((edge) => {
    outgoing.get(edge.source)?.push(edge.target);
    incoming.get(edge.target)?.push(edge.source);
    all.get(edge.source)?.push(edge.target);
    all.get(edge.target)?.push(edge.source);
  });

  return { outgoing, incoming, all };
}

// Degree Centrality
export function degreeCentrality(
  nodes: GraphNode[],
  edges: GraphEdge[]
): Map<string, number> {
  const degrees = new Map<string, number>();
  const maxPossible = nodes.length - 1;

  nodes.forEach((node) => degrees.set(node.id, 0));

  edges.forEach((edge) => {
    degrees.set(edge.source, (degrees.get(edge.source) || 0) + 1);
    degrees.set(edge.target, (degrees.get(edge.target) || 0) + 1);
  });

  // Normalize
  if (maxPossible > 0) {
    degrees.forEach((value, key) => {
      degrees.set(key, value / maxPossible);
    });
  }

  return degrees;
}

// Betweenness Centrality (Brandes algorithm)
export function betweennessCentrality(
  nodes: GraphNode[],
  edges: GraphEdge[]
): Map<string, number> {
  const betweenness = new Map<string, number>();
  const { all: adjacency } = buildAdjacencyList(nodes, edges);
  
  nodes.forEach((node) => betweenness.set(node.id, 0));

  // For each source node
  nodes.forEach((source) => {
    const stack: string[] = [];
    const predecessors = new Map<string, string[]>();
    const sigma = new Map<string, number>();
    const dist = new Map<string, number>();
    const delta = new Map<string, number>();

    nodes.forEach((node) => {
      predecessors.set(node.id, []);
      sigma.set(node.id, 0);
      dist.set(node.id, -1);
      delta.set(node.id, 0);
    });

    sigma.set(source.id, 1);
    dist.set(source.id, 0);

    const queue: string[] = [source.id];

    // BFS
    while (queue.length > 0) {
      const v = queue.shift()!;
      stack.push(v);

      const neighbors = adjacency.get(v) || [];
      for (const w of neighbors) {
        // First time visiting w?
        if (dist.get(w) === -1) {
          dist.set(w, dist.get(v)! + 1);
          queue.push(w);
        }

        // Shortest path to w via v?
        if (dist.get(w) === dist.get(v)! + 1) {
          sigma.set(w, sigma.get(w)! + sigma.get(v)!);
          predecessors.get(w)!.push(v);
        }
      }
    }

    // Accumulation
    while (stack.length > 0) {
      const w = stack.pop()!;
      const preds = predecessors.get(w) || [];

      for (const v of preds) {
        const contribution =
          (sigma.get(v)! / sigma.get(w)!) * (1 + delta.get(w)!);
        delta.set(v, delta.get(v)! + contribution);
      }

      if (w !== source.id) {
        betweenness.set(w, betweenness.get(w)! + delta.get(w)!);
      }
    }
  });

  // Normalize
  const n = nodes.length;
  const normFactor = n > 2 ? 2 / ((n - 1) * (n - 2)) : 1;
  
  betweenness.forEach((value, key) => {
    betweenness.set(key, value * normFactor);
  });

  return betweenness;
}

// Closeness Centrality
export function closenessCentrality(
  nodes: GraphNode[],
  edges: GraphEdge[]
): Map<string, number> {
  const closeness = new Map<string, number>();
  const { all: adjacency } = buildAdjacencyList(nodes, edges);

  nodes.forEach((source) => {
    // BFS to find shortest paths
    const dist = new Map<string, number>();
    nodes.forEach((n) => dist.set(n.id, Infinity));
    dist.set(source.id, 0);

    const queue: string[] = [source.id];

    while (queue.length > 0) {
      const v = queue.shift()!;
      const neighbors = adjacency.get(v) || [];

      for (const w of neighbors) {
        if (dist.get(w) === Infinity) {
          dist.set(w, dist.get(v)! + 1);
          queue.push(w);
        }
      }
    }

    // Sum of distances
    let totalDist = 0;
    let reachable = 0;

    dist.forEach((d) => {
      if (d !== Infinity && d > 0) {
        totalDist += d;
        reachable++;
      }
    });

    // Closeness = reachable / totalDist (normalized)
    const n = nodes.length - 1;
    if (totalDist > 0 && n > 0) {
      closeness.set(source.id, (reachable / n) * (reachable / totalDist));
    } else {
      closeness.set(source.id, 0);
    }
  });

  return closeness;
}

// PageRank Algorithm
export function pageRank(
  nodes: GraphNode[],
  edges: GraphEdge[],
  damping = 0.85,
  iterations = 100,
  tolerance = 1e-6
): Map<string, number> {
  const n = nodes.length;
  const rank = new Map<string, number>();
  const { outgoing, incoming } = buildAdjacencyList(nodes, edges);

  // Initialize ranks
  nodes.forEach((node) => rank.set(node.id, 1 / n));

  for (let i = 0; i < iterations; i++) {
    const newRank = new Map<string, number>();
    let diff = 0;

    nodes.forEach((node) => {
      let incomingRank = 0;
      const incomingNodes = incoming.get(node.id) || [];

      for (const source of incomingNodes) {
        const outDegree = outgoing.get(source)?.length || 1;
        incomingRank += (rank.get(source) || 0) / outDegree;
      }

      const newValue = (1 - damping) / n + damping * incomingRank;
      newRank.set(node.id, newValue);
      diff += Math.abs(newValue - (rank.get(node.id) || 0));
    });

    // Update ranks
    newRank.forEach((value, key) => rank.set(key, value));

    // Check convergence
    if (diff < tolerance) break;
  }

  return rank;
}

// Local Clustering Coefficient
export function clusteringCoefficient(
  nodes: GraphNode[],
  edges: GraphEdge[]
): Map<string, number> {
  const clustering = new Map<string, number>();
  const { all: adjacency } = buildAdjacencyList(nodes, edges);
  const edgeSet = new Set(edges.map((e) => `${e.source}-${e.target}`));

  const hasEdge = (a: string, b: string): boolean => {
    return edgeSet.has(`${a}-${b}`) || edgeSet.has(`${b}-${a}`);
  };

  nodes.forEach((node) => {
    const neighbors = adjacency.get(node.id) || [];
    const k = neighbors.length;

    if (k < 2) {
      clustering.set(node.id, 0);
      return;
    }

    // Count edges between neighbors
    let triangles = 0;
    for (let i = 0; i < neighbors.length; i++) {
      for (let j = i + 1; j < neighbors.length; j++) {
        if (hasEdge(neighbors[i], neighbors[j])) {
          triangles++;
        }
      }
    }

    // Clustering coefficient = 2 * triangles / (k * (k - 1))
    const maxTriangles = (k * (k - 1)) / 2;
    clustering.set(node.id, triangles / maxTriangles);
  });

  return clustering;
}

// Eigenvector Centrality (power iteration)
export function eigenvectorCentrality(
  nodes: GraphNode[],
  edges: GraphEdge[],
  iterations = 100,
  tolerance = 1e-6
): Map<string, number> {
  const centrality = new Map<string, number>();
  const { all: adjacency } = buildAdjacencyList(nodes, edges);

  // Initialize
  nodes.forEach((node) => centrality.set(node.id, 1));

  for (let i = 0; i < iterations; i++) {
    const newCentrality = new Map<string, number>();
    let norm = 0;

    nodes.forEach((node) => {
      let sum = 0;
      const neighbors = adjacency.get(node.id) || [];

      for (const neighbor of neighbors) {
        sum += centrality.get(neighbor) || 0;
      }

      newCentrality.set(node.id, sum);
      norm += sum * sum;
    });

    norm = Math.sqrt(norm);
    if (norm === 0) norm = 1;

    // Normalize and check convergence
    let diff = 0;
    newCentrality.forEach((value, key) => {
      const normalized = value / norm;
      diff += Math.abs(normalized - (centrality.get(key) || 0));
      centrality.set(key, normalized);
    });

    if (diff < tolerance) break;
  }

  return centrality;
}

// Community Detection (Label Propagation)
export function labelPropagation(
  nodes: GraphNode[],
  edges: GraphEdge[],
  iterations = 100
): Map<string, number> {
  const labels = new Map<string, number>();
  const { all: adjacency } = buildAdjacencyList(nodes, edges);

  // Initialize each node with unique label
  nodes.forEach((node, index) => labels.set(node.id, index));

  for (let i = 0; i < iterations; i++) {
    let changed = false;

    // Shuffle nodes
    const shuffled = [...nodes].sort(() => Math.random() - 0.5);

    for (const node of shuffled) {
      const neighbors = adjacency.get(node.id) || [];
      if (neighbors.length === 0) continue;

      // Count neighbor labels
      const labelCounts = new Map<number, number>();
      for (const neighbor of neighbors) {
        const label = labels.get(neighbor)!;
        labelCounts.set(label, (labelCounts.get(label) || 0) + 1);
      }

      // Find most common label
      let maxCount = 0;
      let maxLabels: number[] = [];
      labelCounts.forEach((count, label) => {
        if (count > maxCount) {
          maxCount = count;
          maxLabels = [label];
        } else if (count === maxCount) {
          maxLabels.push(label);
        }
      });

      // Randomly select among ties
      const newLabel = maxLabels[Math.floor(Math.random() * maxLabels.length)];

      if (newLabel !== labels.get(node.id)) {
        labels.set(node.id, newLabel);
        changed = true;
      }
    }

    if (!changed) break;
  }

  return labels;
}

// Calculate all SNA metrics
export function calculateAllMetrics(
  nodes: GraphNode[],
  edges: GraphEdge[]
): SNAMetrics {
  return {
    degree: degreeCentrality(nodes, edges),
    betweenness: betweennessCentrality(nodes, edges),
    closeness: closenessCentrality(nodes, edges),
    pageRank: pageRank(nodes, edges),
    clustering: clusteringCoefficient(nodes, edges),
  };
}

// Find shortest path between two nodes (BFS)
export function shortestPath(
  nodes: GraphNode[],
  edges: GraphEdge[],
  sourceId: string,
  targetId: string
): string[] | null {
  const { all: adjacency } = buildAdjacencyList(nodes, edges);
  const visited = new Set<string>();
  const parent = new Map<string, string | null>();
  const queue: string[] = [sourceId];

  visited.add(sourceId);
  parent.set(sourceId, null);

  while (queue.length > 0) {
    const current = queue.shift()!;

    if (current === targetId) {
      // Reconstruct path
      const path: string[] = [];
      let node: string | null = targetId;
      
      while (node !== null) {
        path.unshift(node);
        node = parent.get(node) ?? null;
      }
      
      return path;
    }

    const neighbors = adjacency.get(current) || [];
    for (const neighbor of neighbors) {
      if (!visited.has(neighbor)) {
        visited.add(neighbor);
        parent.set(neighbor, current);
        queue.push(neighbor);
      }
    }
  }

  return null; // No path found
}

// Find all connected components
export function connectedComponents(
  nodes: GraphNode[],
  edges: GraphEdge[]
): Map<string, number> {
  const components = new Map<string, number>();
  const { all: adjacency } = buildAdjacencyList(nodes, edges);
  const visited = new Set<string>();
  let componentId = 0;

  nodes.forEach((startNode) => {
    if (visited.has(startNode.id)) return;

    // BFS from this node
    const queue: string[] = [startNode.id];
    visited.add(startNode.id);

    while (queue.length > 0) {
      const current = queue.shift()!;
      components.set(current, componentId);

      const neighbors = adjacency.get(current) || [];
      for (const neighbor of neighbors) {
        if (!visited.has(neighbor)) {
          visited.add(neighbor);
          queue.push(neighbor);
        }
      }
    }

    componentId++;
  });

  return components;
}
