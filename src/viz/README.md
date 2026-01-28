# NVL Graph

A graph visualization library built with [Neo4j NVL](https://neo4j.com/docs/nvl/current/) for GPU-accelerated rendering.

## Installation

```bash
npm install
npm run dev
```

## Features

- **Multiple Layouts**: Force-directed, hierarchical (dagre), grid, radial, circular, concentric
- **Social Network Analysis**: Degree, betweenness, closeness centrality, PageRank, clustering coefficient
- **Community Detection**: Label propagation algorithm for grouping related nodes
- **Path Finding**: Shortest path highlighting between nodes
- **Time Bar**: Filter nodes/edges by timestamp
- **Combos**: Group nodes by attribute into collapsible containers
- **Custom Styling**: Node colors, sizes, icons, glyphs, edge styles (solid/dashed/dotted)
- **Dark/Light Themes**: Built-in theme support
- **Performance Optimizations**: Automatic simplification for large graphs (100+ nodes)

## Project Structure

```
src/
├── components/Graph/
│   ├── Chart.tsx      # Main chart component with NVL integration
│   ├── TimeBar.tsx    # Temporal filtering component
│   └── index.ts       # Public exports
├── utils/
│   ├── layouts.ts     # Layout algorithms (dagre, d3-force)
│   └── sna.ts         # Social network analysis algorithms
├── samples/
│   └── index.ts       # Sample data generators (code intel, social, knowledge graph, infra)
├── types.ts           # TypeScript type definitions
├── App.tsx            # Demo application
└── index.tsx          # Entry point
```

## Usage

```tsx
import { Chart, GraphNode, GraphEdge } from './components/Graph';

const nodes: GraphNode[] = [
  { id: '1', label: 'Node 1', color: '#6366f1', size: 50 },
  { id: '2', label: 'Node 2', color: '#10b981', size: 40 },
];

const edges: GraphEdge[] = [
  { id: 'e1', source: '1', target: '2', label: 'connects' },
];

function App() {
  return (
    <Chart
      nodes={nodes}
      edges={edges}
      config={{ layout: { type: 'force' }, theme: 'dark' }}
      onNodeClick={(node) => console.log('Clicked:', node)}
      showTimeBar={true}
      showMiniMap={true}
    />
  );
}
```

## Chart API

### Props

| Prop | Type | Description |
|------|------|-------------|
| `nodes` | `GraphNode[]` | Array of nodes to render |
| `edges` | `GraphEdge[]` | Array of edges connecting nodes |
| `config` | `ChartConfig` | Layout, theme, and behavior options |
| `onNodeClick` | `(node: GraphNode) => void` | Node click handler |
| `onEdgeClick` | `(edge: GraphEdge) => void` | Edge click handler |
| `showTimeBar` | `boolean` | Show temporal filter bar |
| `showMiniMap` | `boolean` | Show navigation minimap |
| `showControls` | `boolean` | Show zoom controls |

### Ref Methods

```tsx
const chartRef = useRef<ChartRef>(null);

chartRef.current?.fitView();                    // Fit all nodes in view
chartRef.current?.setLayout({ type: 'hierarchical', direction: 'TB' });
chartRef.current?.highlightPath('node1', 'node2');  // Highlight shortest path
chartRef.current?.colorByMetric('betweenness'); // Color nodes by SNA metric
chartRef.current?.colorByCommunity();           // Color by detected communities
chartRef.current?.scaleByConnections(true);     // Scale node sizes by degree
chartRef.current?.groupByAttribute('department'); // Group nodes into combos
chartRef.current?.getMetrics();                 // Get SNA metrics
```

## Types

### GraphNode

```ts
interface GraphNode {
  id: string;
  label?: string;
  size?: number;
  color?: string;
  icon?: string;
  glyphs?: Glyph[];
  data?: Record<string, unknown>;
  timestamp?: number | Date;
  combo?: string;
}
```

### GraphEdge

```ts
interface GraphEdge {
  id: string;
  source: string;
  target: string;
  label?: string;
  color?: string;
  width?: number;
  style?: 'solid' | 'dashed' | 'dotted';
  timestamp?: number | Date;
}
```

## Sample Datasets

The library includes sample data generators for testing:

- **Code Intelligence**: Namespaces, classes, methods, call relationships
- **Social Network**: Users with friend/follow connections
- **Knowledge Graph**: Entities with typed relationships
- **Infrastructure**: Servers, services, dependencies

```tsx
import { samples, getSampleList } from './samples';

const codeGraph = samples.codeSmall();
const socialGraph = samples.socialMedium();
```

## Dependencies

- `@neo4j-nvl/base` / `@neo4j-nvl/react` - GPU-accelerated graph rendering
- `dagre` - Hierarchical layout algorithm
- `d3-force` - Force-directed layout simulation
- `d3-hierarchy` - Tree/hierarchy layouts
