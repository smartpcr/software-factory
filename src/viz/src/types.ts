// Graph Visualization Types - NVL Integration

// NVL Node type (compatible with @neo4j-nvl/base)
export interface NVLNode {
  id: string;
  size?: number;
  color?: string;
  caption?: string;
  captionColor?: string;
  captionSize?: number;
  icon?: string;
  iconColor?: string;
  pinned?: boolean;
  x?: number;
  y?: number;
  selected?: boolean;
  highlighted?: boolean;
  disabled?: boolean;
  hovered?: boolean;
}

// NVL Relationship type (compatible with @neo4j-nvl/base)
export interface NVLRelationship {
  id: string;
  from: string;
  to: string;
  caption?: string;
  captionColor?: string;
  captionSize?: number;
  color?: string;
  width?: number;
  selected?: boolean;
  highlighted?: boolean;
  disabled?: boolean;
}

export interface GraphNode {
  id: string;
  label?: string;
  type?: 'default' | 'combo' | 'image';
  size?: number;
  color?: string;
  borderColor?: string;
  borderWidth?: number;
  image?: string;
  icon?: string;
  glyphs?: Glyph[];
  data?: Record<string, unknown>;
  timestamp?: number | Date;
  combo?: string; // Parent combo ID
  x?: number;
  y?: number;
}

export interface GraphEdge {
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

export interface Glyph {
  position: 'topLeft' | 'topRight' | 'bottomLeft' | 'bottomRight';
  color?: string;
  text?: string;
  icon?: string;
}

export interface Combo {
  id: string;
  label?: string;
  color?: string;
  borderColor?: string;
  collapsed?: boolean;
  padding?: number;
}

export interface Annotation {
  id: string;
  type: 'text' | 'shape' | 'arrow';
  x: number;
  y: number;
  text?: string;
  color?: string;
  width?: number;
  height?: number;
}

export type LayoutType = 
  | 'force'
  | 'hierarchical'
  | 'radial'
  | 'circular'
  | 'grid'
  | 'dagre'
  | 'concentric';

export interface LayoutOptions {
  type: LayoutType;
  direction?: 'TB' | 'BT' | 'LR' | 'RL';
  spacing?: number;
  nodeSpacing?: number;
  rankSpacing?: number;
  iterations?: number;
  strength?: number;
  center?: { x: number; y: number };
}

export interface TimeRange {
  start: number | Date;
  end: number | Date;
}

export interface SNAMetrics {
  degree: Map<string, number>;
  betweenness: Map<string, number>;
  closeness: Map<string, number>;
  pageRank: Map<string, number>;
  clustering: Map<string, number>;
}

export interface ChartConfig {
  layout?: LayoutOptions;
  theme?: 'light' | 'dark';
  fitView?: boolean;
  zoomOnScroll?: boolean;
  panOnDrag?: boolean;
  selectionOnDrag?: boolean;
  minZoom?: number;
  maxZoom?: number;
  defaultNodeSize?: number;
  defaultEdgeWidth?: number;
  animationDuration?: number;
  /** Scale node sizes based on their connection count (degree) */
  scaleNodesByConnections?: boolean;
  /** Minimum node size when scaling by connections */
  minNodeSize?: number;
  /** Maximum node size when scaling by connections */
  maxNodeSize?: number;
  /** Threshold for enabling performance mode (simplified rendering) */
  performanceThreshold?: number;
  /** Threshold for very large graph optimizations (disable minimap, metrics, etc.) */
  veryLargeGraphThreshold?: number;
}

export interface GraphSelection {
  nodes: string[];
  edges: string[];
}

export interface GraphEvent {
  type: 'click' | 'doubleClick' | 'hover' | 'select' | 'contextMenu';
  target: 'node' | 'edge' | 'canvas' | 'combo';
  id?: string;
  data?: unknown;
  position?: { x: number; y: number };
}

// Alias for backward compatibility
export type GraphConfig = ChartConfig;
