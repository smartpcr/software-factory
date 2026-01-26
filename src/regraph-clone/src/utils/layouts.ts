// Layout Algorithms for ReGraph Clone

import dagre from 'dagre';
import {
  forceSimulation,
  forceLink,
  forceManyBody,
  forceCenter,
  forceCollide,
  SimulationNodeDatum,
  SimulationLinkDatum,
} from 'd3-force';
import type { LayoutOptions } from '../types';

// Generic node type for layouts (compatible with both xyflow and NVL)
export interface LayoutNode {
  id: string;
  position: { x: number; y: number };
  measured?: { width?: number; height?: number };
  data?: Record<string, unknown>;
}

// Generic edge type for layouts
export interface LayoutEdge {
  id: string;
  source: string;
  target: string;
}

interface SimLayoutNode extends SimulationNodeDatum {
  id: string;
  width: number;
  height: number;
}

interface SimLayoutLink extends SimulationLinkDatum<SimLayoutNode> {
  id: string;
}

// Force-directed layout using d3-force
export function forceLayout(
  nodes: LayoutNode[],
  edges: LayoutEdge[],
  options: LayoutOptions
): LayoutNode[] {
  const nodeCount = nodes.length;

  // Adaptive parameters based on graph size for performance
  const isLarge = nodeCount > 100;
  const isVeryLarge = nodeCount > 500;

  // Reduce iterations and adjust forces for large graphs
  const defaultIterations = isVeryLarge ? 100 : isLarge ? 200 : 500;
  const defaultStrength = isVeryLarge ? -200 : isLarge ? -400 : -800;
  const defaultSpacing = isVeryLarge ? 100 : isLarge ? 150 : 250;

  const {
    strength = defaultStrength,
    iterations = defaultIterations,
    spacing = defaultSpacing
  } = options;

  // Calculate optimal spacing based on node count
  const linkDistance = isVeryLarge
    ? 50 + Math.sqrt(nodeCount) * 5
    : Math.max(spacing, 150 + nodeCount * 5);
  const collideRadius = isVeryLarge
    ? 30 + Math.sqrt(nodeCount)
    : Math.max(100, 60 + nodeCount * 2);

  const simNodes: SimLayoutNode[] = nodes.map((node, index) => {
    // Initialize with circular layout for better convergence
    const angle = (2 * Math.PI * index) / nodeCount;
    const radius = nodeCount * 30;
    return {
      id: node.id,
      x: 400 + radius * Math.cos(angle),
      y: 300 + radius * Math.sin(angle),
      width: node.measured?.width || 150,
      height: node.measured?.height || 50,
    };
  });

  const simLinks: SimLayoutLink[] = edges.map((edge) => ({
    id: edge.id,
    source: edge.source,
    target: edge.target,
  }));

  const simulation = forceSimulation(simNodes)
    .force('link', forceLink(simLinks)
      .id((d: any) => d.id)
      .distance(linkDistance)
      .strength(0.7))
    .force('charge', forceManyBody()
      .strength(strength)
      .distanceMin(50)
      .distanceMax(1000))
    .force('center', forceCenter(400, 300))
    .force('collide', forceCollide()
      .radius(collideRadius)
      .strength(1)
      .iterations(3))
    .alphaDecay(0.01)
    .velocityDecay(0.3)
    .stop();

  // Run more iterations for better convergence
  for (let i = 0; i < iterations; i++) {
    simulation.tick();
  }

  return nodes.map((node) => {
    const simNode = simNodes.find((n) => n.id === node.id);
    return {
      ...node,
      position: {
        x: simNode?.x || 0,
        y: simNode?.y || 0,
      },
    };
  });
}

// Hierarchical layout using dagre
export function hierarchicalLayout(
  nodes: LayoutNode[],
  edges: LayoutEdge[],
  options: LayoutOptions
): LayoutNode[] {
  const {
    direction = 'TB',
    nodeSpacing = 50,
    rankSpacing = 100,
  } = options;

  const g = new dagre.graphlib.Graph();
  g.setGraph({
    rankdir: direction,
    nodesep: nodeSpacing,
    ranksep: rankSpacing,
    marginx: 50,
    marginy: 50,
  });
  g.setDefaultEdgeLabel(() => ({}));

  nodes.forEach((node) => {
    g.setNode(node.id, {
      width: node.measured?.width || 150,
      height: node.measured?.height || 50,
    });
  });

  edges.forEach((edge) => {
    g.setEdge(edge.source, edge.target);
  });

  dagre.layout(g);

  return nodes.map((node) => {
    const nodeWithPosition = g.node(node.id);
    return {
      ...node,
      position: {
        x: nodeWithPosition.x - (node.measured?.width || 150) / 2,
        y: nodeWithPosition.y - (node.measured?.height || 50) / 2,
      },
    };
  });
}

// Radial layout
export function radialLayout(
  nodes: LayoutNode[],
  edges: LayoutEdge[],
  options: LayoutOptions
): LayoutNode[] {
  const { spacing = 100, center = { x: 400, y: 300 } } = options;
  
  // Find root node (node with no incoming edges)
  const targetSet = new Set(edges.map((e) => e.target));
  const rootNode = nodes.find((n) => !targetSet.has(n.id)) || nodes[0];
  
  // Build adjacency list
  const adjacency = new Map<string, string[]>();
  nodes.forEach((n) => adjacency.set(n.id, []));
  edges.forEach((e) => {
    adjacency.get(e.source)?.push(e.target);
  });

  // BFS to assign levels
  const levels = new Map<string, number>();
  const queue: string[] = [rootNode?.id || nodes[0]?.id];
  levels.set(queue[0], 0);
  
  while (queue.length > 0) {
    const current = queue.shift()!;
    const currentLevel = levels.get(current)!;
    const neighbors = adjacency.get(current) || [];
    
    for (const neighbor of neighbors) {
      if (!levels.has(neighbor)) {
        levels.set(neighbor, currentLevel + 1);
        queue.push(neighbor);
      }
    }
  }

  // Group nodes by level
  const levelGroups = new Map<number, string[]>();
  levels.forEach((level, nodeId) => {
    if (!levelGroups.has(level)) {
      levelGroups.set(level, []);
    }
    levelGroups.get(level)!.push(nodeId);
  });

  // Position nodes
  return nodes.map((node) => {
    const level = levels.get(node.id) ?? 0;
    const nodesAtLevel = levelGroups.get(level) || [node.id];
    const indexAtLevel = nodesAtLevel.indexOf(node.id);
    const angleStep = (2 * Math.PI) / nodesAtLevel.length;
    const angle = indexAtLevel * angleStep - Math.PI / 2;
    const radius = level * spacing;

    return {
      ...node,
      position: {
        x: center.x + radius * Math.cos(angle),
        y: center.y + radius * Math.sin(angle),
      },
    };
  });
}

// Circular layout
export function circularLayout(
  nodes: LayoutNode[],
  _edges: LayoutEdge[],
  options: LayoutOptions
): LayoutNode[] {
  const { spacing = 200, center = { x: 400, y: 300 } } = options;
  const angleStep = (2 * Math.PI) / nodes.length;
  const radius = (nodes.length * spacing) / (2 * Math.PI);

  return nodes.map((node, index) => {
    const angle = index * angleStep - Math.PI / 2;
    return {
      ...node,
      position: {
        x: center.x + radius * Math.cos(angle),
        y: center.y + radius * Math.sin(angle),
      },
    };
  });
}

// Grid layout
export function gridLayout(
  nodes: LayoutNode[],
  _edges: LayoutEdge[],
  options: LayoutOptions
): LayoutNode[] {
  const { spacing = 200 } = options;
  const cols = Math.ceil(Math.sqrt(nodes.length));

  return nodes.map((node, index) => {
    const row = Math.floor(index / cols);
    const col = index % cols;
    return {
      ...node,
      position: {
        x: col * spacing + 100,
        y: row * spacing + 100,
      },
    };
  });
}

// Concentric layout (nodes grouped by a metric)
export function concentricLayout(
  nodes: LayoutNode[],
  edges: LayoutEdge[],
  options: LayoutOptions,
  metric?: Map<string, number>
): LayoutNode[] {
  const { spacing = 100, center = { x: 400, y: 300 } } = options;

  // Group by metric value or use degree centrality
  const degrees = new Map<string, number>();
  nodes.forEach((n) => degrees.set(n.id, 0));
  edges.forEach((e) => {
    degrees.set(e.source, (degrees.get(e.source) || 0) + 1);
    degrees.set(e.target, (degrees.get(e.target) || 0) + 1);
  });

  const nodeMetric = metric || degrees;
  const sortedNodes = [...nodes].sort(
    (a, b) => (nodeMetric.get(b.id) || 0) - (nodeMetric.get(a.id) || 0)
  );

  // Group into rings
  const rings: string[][] = [];
  const maxDegree = Math.max(...Array.from(nodeMetric.values()));
  
  sortedNodes.forEach((node) => {
    const value = nodeMetric.get(node.id) || 0;
    const ringIndex = maxDegree > 0 
      ? Math.floor((1 - value / maxDegree) * 4) 
      : 0;
    
    if (!rings[ringIndex]) rings[ringIndex] = [];
    rings[ringIndex].push(node.id);
  });

  // Position nodes in rings
  const positions = new Map<string, { x: number; y: number }>();
  
  rings.forEach((ring, ringIndex) => {
    const radius = (ringIndex + 1) * spacing;
    const angleStep = (2 * Math.PI) / ring.length;
    
    ring.forEach((nodeId, index) => {
      const angle = index * angleStep - Math.PI / 2;
      positions.set(nodeId, {
        x: center.x + radius * Math.cos(angle),
        y: center.y + radius * Math.sin(angle),
      });
    });
  });

  return nodes.map((node) => ({
    ...node,
    position: positions.get(node.id) || { x: center.x, y: center.y },
  }));
}

// Main layout function
export function applyLayout(
  nodes: LayoutNode[],
  edges: LayoutEdge[],
  options: LayoutOptions
): LayoutNode[] {
  if (nodes.length === 0) return nodes;

  switch (options.type) {
    case 'force':
      return forceLayout(nodes, edges, options);
    case 'hierarchical':
    case 'dagre':
      return hierarchicalLayout(nodes, edges, options);
    case 'radial':
      return radialLayout(nodes, edges, options);
    case 'circular':
      return circularLayout(nodes, edges, options);
    case 'grid':
      return gridLayout(nodes, edges, options);
    case 'concentric':
      return concentricLayout(nodes, edges, options);
    default:
      return hierarchicalLayout(nodes, edges, options);
  }
}
