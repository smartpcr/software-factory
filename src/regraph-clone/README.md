# ReGraph Clone

An open-source alternative to Cambridge Intelligence's ReGraph, built with React Flow. This library provides powerful graph visualization capabilities for React applications.

## Features

- **Multiple Layouts**: Force-directed, hierarchical, radial, circular, grid, and concentric layouts
- **Social Network Analysis (SNA)**: Built-in algorithms for degree, betweenness, closeness centrality, PageRank, and clustering coefficient
- **Time Bar**: Filter nodes and edges by timestamp with an interactive histogram
- **Combos**: Group nodes into collapsible containers
- **Custom Styling**: Full control over node and edge appearance, including glyphs
- **Path Finding**: Highlight shortest paths between nodes
- **Community Detection**: Color nodes by community using label propagation
- **Dark/Light Themes**: Built-in theme support
- **Interactive Controls**: Pan, zoom, selection, minimap

## Installation

```bash
npm install regraph-clone
```

## Quick Start

```tsx
import React, { useRef } from 'react';
import { Chart, ChartRef, ReGraphNode, ReGraphEdge } from 'regraph-clone';

const nodes: ReGraphNode[] = [
  { id: 'a', label: 'Node A', color: '#6366f1' },
  { id: 'b', label: 'Node B', color: '#10b981' },
  { id: 'c', label: 'Node C', color: '#f59e0b' },
];

const edges: ReGraphEdge[] = [
  { id: 'e1', source: 'a', target: 'b', label: 'connects' },
  { id: 'e2', source: 'b', target: 'c' },
];

function App() {
  const chartRef = useRef<ChartRef>(null);

  return (
    <div style={{ width: '100%', height: '100vh' }}>
      <Chart
        ref={chartRef}
        nodes={nodes}
        edges={edges}
        config={{
          layout: { type: 'force' },
          theme: 'light',
          fitView: true,
        }}
        onNodeClick={(node) => console.log('Clicked:', node)}
        showTimeBar={false}
        showMiniMap={true}
      />
    </div>
  );
}
```

## API Reference

### Chart Component

```tsx
interface ChartProps {
  nodes: ReGraphNode[];
  edges: ReGraphEdge[];
  config?: ReGraphConfig;
  onNodeClick?: (node: ReGraphNode) => void;
  onEdgeClick?: (edge: ReGraphEdge) => void;
  onSelectionChange?: (nodeIds: string[], edgeIds: string[]) => void;
  showTimeBar?: boolean;
  showMiniMap?: boolean;
  showControls?: boolean;
  className?: string;
  style?: React.CSSProperties;
}
```

### Chart Ref Methods

```tsx
interface ChartRef {
  fitView: () => void;
  setLayout: (options: LayoutOptions) => void;
  getMetrics: () => SNAMetrics | null;
  highlightPath: (sourceId: string, targetId: string) => void;
  clearHighlight: () => void;
  colorByCommunity: () => void;
  colorByMetric: (metric: keyof SNAMetrics) => void;
  exportImage: () => Promise<string>;
  getSelectedNodes: () => string[];
  getSelectedEdges: () => string[];
}
```

### Node Properties

```tsx
interface ReGraphNode {
  id: string;
  label?: string;
  type?: 'default' | 'combo' | 'image';
  size?: number;
  color?: string;
  borderColor?: string;
  borderWidth?: number;
  image?: string;
  icon?: string;  // Emoji or icon character
  glyphs?: Glyph[];
  data?: Record<string, unknown>;
  timestamp?: number | Date;
  combo?: string;  // Parent combo ID
  x?: number;
  y?: number;
}
```

### Edge Properties

```tsx
interface ReGraphEdge {
  id: string;
  source: string;
  target: string;
  label?: string;
  color?: string;
  width?: number;
  style?: 'solid' | 'dashed' | 'dotted';
  animated?: boolean;
  data?: Record<string, unknown>;
  timestamp?: number | Date;
}
```

## Layouts

Available layout types:

| Layout | Description |
|--------|-------------|
| `force` | Force-directed layout using d3-force |
| `hierarchical` | Tree-like layout using dagre |
| `radial` | Nodes arranged in concentric circles |
| `circular` | Nodes on a single circle |
| `grid` | Regular grid arrangement |
| `concentric` | Nodes grouped by centrality metric |

```tsx
// Change layout programmatically
chartRef.current?.setLayout({
  type: 'hierarchical',
  direction: 'TB', // TB, BT, LR, RL
  nodeSpacing: 50,
  rankSpacing: 100,
});
```

## Social Network Analysis

Built-in SNA algorithms:

- **Degree Centrality**: Number of connections (normalized)
- **Betweenness Centrality**: How often a node lies on shortest paths
- **Closeness Centrality**: Average distance to all other nodes
- **PageRank**: Importance based on incoming links
- **Clustering Coefficient**: How connected a node's neighbors are
- **Community Detection**: Label propagation algorithm

```tsx
// Get all metrics
const metrics = chartRef.current?.getMetrics();

// Color nodes by a metric
chartRef.current?.colorByMetric('betweenness');

// Color by community
chartRef.current?.colorByCommunity();
```

## Time Bar

Filter graph elements by timestamp:

```tsx
const nodes = [
  { id: 'a', label: 'Event A', timestamp: new Date('2024-01-01') },
  { id: 'b', label: 'Event B', timestamp: new Date('2024-01-15') },
];

<Chart
  nodes={nodes}
  edges={edges}
  showTimeBar={true}
/>
```

## Glyphs

Add badges/indicators to nodes:

```tsx
const node = {
  id: 'user1',
  label: 'Alice',
  glyphs: [
    { position: 'topRight', color: '#22c55e', text: '✓' },
    { position: 'bottomRight', color: '#ef4444', text: '5' },
  ],
};
```

Positions: `topLeft`, `topRight`, `bottomLeft`, `bottomRight`

## Path Finding

Highlight the shortest path between two nodes:

```tsx
chartRef.current?.highlightPath('nodeA', 'nodeZ');

// Clear highlighting
chartRef.current?.clearHighlight();
```

## Theming

```tsx
<Chart
  config={{
    theme: 'dark', // or 'light'
  }}
/>
```

## Standalone Utilities

Use the SNA algorithms independently:

```tsx
import {
  degreeCentrality,
  betweennessCentrality,
  pageRank,
  shortestPath,
  connectedComponents,
  labelPropagation,
} from 'regraph-clone';

const degree = degreeCentrality(nodes, edges);
const path = shortestPath(nodes, edges, 'a', 'z');
const communities = labelPropagation(nodes, edges);
```

## Development

```bash
# Install dependencies
npm install

# Start dev server
npm run dev

# Build for production
npm run build

# Type check
npm run typecheck
```

## Comparison with ReGraph

| Feature | ReGraph Clone | Cambridge ReGraph |
|---------|---------------|-------------------|
| License | MIT (Open Source) | Commercial |
| React Support | ✅ | ✅ |
| Layouts | 6 types | Multiple |
| SNA Algorithms | 5+ built-in | Yes |
| Time Bar | ✅ | ✅ |
| Combos | ✅ | ✅ |
| Map Mode | Planned | ✅ |
| GPU Rendering | Via React Flow | WebGL |
| Cost | Free | License required |

## License

MIT License - feel free to use in personal and commercial projects.

## Credits

Built with:
- [React Flow](https://reactflow.dev/) - Core graph rendering
- [d3-force](https://github.com/d3/d3-force) - Force-directed layout
- [dagre](https://github.com/dagrejs/dagre) - Hierarchical layout
