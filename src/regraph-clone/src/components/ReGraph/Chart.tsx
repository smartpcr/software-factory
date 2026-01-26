// Main Chart Component for ReGraph Clone - NVL Integration
// This is the primary component that combines all features using Neo4j NVL

import React, {
  useState,
  useCallback,
  useMemo,
  useEffect,
  forwardRef,
  useImperativeHandle,
  useRef,
} from 'react';
import { InteractiveNvlWrapper } from '@neo4j-nvl/react';
import type { MouseEventCallbacks } from '@neo4j-nvl/react';
import type { Node as NVLNodeType, Relationship as NVLRelType, HitTargets, Layout as NVLLayout } from '@neo4j-nvl/base';
import NVL from '@neo4j-nvl/base';

import TimeBar from './TimeBar';
import {
  calculateAllMetrics,
  shortestPath,
  labelPropagation,
} from '../../utils/sna';
import type {
  ReGraphNode,
  ReGraphEdge,
  LayoutOptions,
  ReGraphConfig,
  SNAMetrics,
  TimeRange,
  NVLNode,
  NVLRelationship,
} from '../../types';

// Default performance thresholds
const DEFAULT_LARGE_GRAPH_THRESHOLD = 100;
const DEFAULT_VERY_LARGE_GRAPH_THRESHOLD = 500;

// Map our layout types to NVL layout types
// NVL supports: forceDirected, hierarchical, grid, free, d3Force
function mapLayoutToNVL(layoutType: string): NVLLayout {
  switch (layoutType) {
    case 'force':
      return 'forceDirected'; // NVL's GPU-accelerated force simulation
    case 'hierarchical':
    case 'dagre':
      return 'hierarchical'; // Dagre-based, best for minimizing edge crossings
    case 'grid':
      return 'grid'; // Regular grid arrangement
    case 'd3Force':
      return 'd3Force'; // D3's force simulation (CPU-based, more customizable)
    case 'free':
      return 'free'; // Manual positioning only
    default:
      return 'forceDirected';
  }
}

export interface ChartProps {
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

export interface ChartRef {
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
  scaleByConnections: (enabled: boolean) => void;
  groupByAttribute: (attribute: string) => void;
  ungroupAll: () => void;
}

// Calculate node degrees (connection counts)
function calculateDegrees(edges: ReGraphEdge[]): Map<string, number> {
  const degrees = new Map<string, number>();
  edges.forEach((edge) => {
    degrees.set(edge.source, (degrees.get(edge.source) || 0) + 1);
    degrees.set(edge.target, (degrees.get(edge.target) || 0) + 1);
  });
  return degrees;
}

// Scale node size based on degree
function scaleNodeSize(
  degree: number,
  minDegree: number,
  maxDegree: number,
  minSize: number,
  maxSize: number
): number {
  if (maxDegree === minDegree) return (minSize + maxSize) / 2;
  const normalized = (degree - minDegree) / (maxDegree - minDegree);
  return minSize + normalized * (maxSize - minSize);
}

// Convert ReGraph nodes to NVL format
function toNVLNode(node: ReGraphNode): NVLNode {
  return {
    id: node.id,
    size: node.size || 30,
    color: node.color || '#6366f1',
    caption: node.label,
    icon: node.icon,
  };
}

// Convert ReGraph edges to NVL relationships
function toNVLRelationship(edge: ReGraphEdge): NVLRelationship {
  return {
    id: edge.id,
    from: edge.source,
    to: edge.target,
    caption: edge.label,
    color: edge.color || '#94a3b8',
    width: edge.width || 1,
  };
}

const Chart = forwardRef<ChartRef, ChartProps>(
  (
    {
      nodes: inputNodes,
      edges: inputEdges,
      config = {},
      onNodeClick,
      onEdgeClick,
      onSelectionChange,
      showTimeBar = false,
      className,
      style,
    },
    ref
  ) => {
    // NVL instance ref
    const nvlRef = useRef<NVL | null>(null);

    // Ref to track if initial fit has been done (reset on data/layout change)
    const hasInitialFit = useRef(false);

    // Ref to track metrics for imperative methods
    const metricsRef = useRef<SNAMetrics | null>(null);

    // State
    const [nvlNodes, setNvlNodes] = useState<NVLNode[]>([]);
    const [nvlRelationships, setNvlRelationships] = useState<NVLRelationship[]>([]);
    const [metrics, setMetrics] = useState<SNAMetrics | null>(null);
    const [timeRange, setTimeRange] = useState<TimeRange | null>(null);
    const [highlightedNodes, setHighlightedNodes] = useState<Set<string>>(new Set());
    const [highlightedEdges, setHighlightedEdges] = useState<Set<string>>(new Set());
    const [selectedNodes, setSelectedNodes] = useState<string[]>([]);
    const [selectedEdges, setSelectedEdges] = useState<string[]>([]);
    const [originalNodeSizes, setOriginalNodeSizes] = useState<Map<string, number>>(new Map());
    const [currentNvlLayout, setCurrentNvlLayout] = useState<NVLLayout>('forceDirected');

    // Config defaults
    const {
      layout = { type: 'force' },
      theme = 'light',
      scaleNodesByConnections = false,
      minNodeSize = 20,
      maxNodeSize = 60,
      veryLargeGraphThreshold = DEFAULT_VERY_LARGE_GRAPH_THRESHOLD,
    } = config;

    // Initialize nodes and edges
    useEffect(() => {
      const nodeCount = inputNodes.length;
      const isVeryLarge = nodeCount > veryLargeGraphThreshold;

      let nodes = inputNodes.map(toNVLNode);
      const relationships = inputEdges.map(toNVLRelationship);

      // Store original node sizes
      const sizes = new Map<string, number>();
      nodes.forEach((node) => {
        sizes.set(node.id, node.size || 30);
      });
      setOriginalNodeSizes(sizes);

      // Apply node size scaling based on connections if enabled
      if (scaleNodesByConnections) {
        const degrees = calculateDegrees(inputEdges);
        const degreeValues = Array.from(degrees.values());
        const minDeg = Math.min(...degreeValues, 0);
        const maxDeg = Math.max(...degreeValues, 1);

        nodes = nodes.map((node) => ({
          ...node,
          size: scaleNodeSize(
            degrees.get(node.id) || 0,
            minDeg,
            maxDeg,
            minNodeSize,
            maxNodeSize
          ),
        }));
      }

      setNvlNodes(nodes);
      setNvlRelationships(relationships);

      // Set NVL layout based on config
      const nvlLayout = mapLayoutToNVL(layout.type);
      setCurrentNvlLayout(nvlLayout);

      // Reset fit flag so it will fit again with new data/layout
      hasInitialFit.current = false;

      // Calculate metrics (skip for very large graphs)
      if (!isVeryLarge) {
        const newMetrics = calculateAllMetrics(inputNodes, inputEdges);
        setMetrics(newMetrics);
        metricsRef.current = newMetrics;
      } else {
        setMetrics(null);
        metricsRef.current = null;
      }
    }, [inputNodes, inputEdges, layout.type, scaleNodesByConnections, minNodeSize, maxNodeSize, veryLargeGraphThreshold]);

    // Apply time filter
    useEffect(() => {
      if (!timeRange) return;

      setNvlNodes((nodes) =>
        nodes.map((node) => {
          const originalNode = inputNodes.find((n) => n.id === node.id);
          if (!originalNode?.timestamp) return node;

          const ts =
            typeof originalNode.timestamp === 'number'
              ? originalNode.timestamp
              : originalNode.timestamp.getTime();

          const rangeStart = typeof timeRange.start === 'number' ? timeRange.start : (timeRange.start as Date).getTime();
          const rangeEnd = typeof timeRange.end === 'number' ? timeRange.end : (timeRange.end as Date).getTime();
          const inRange = ts >= rangeStart && ts <= rangeEnd;

          return {
            ...node,
            disabled: !inRange,
          };
        })
      );

      setNvlRelationships((rels) =>
        rels.map((rel) => {
          const originalEdge = inputEdges.find((e) => e.id === rel.id);
          if (!originalEdge?.timestamp) return rel;

          const ts =
            typeof originalEdge.timestamp === 'number'
              ? originalEdge.timestamp
              : originalEdge.timestamp.getTime();

          const rangeStart = typeof timeRange.start === 'number' ? timeRange.start : (timeRange.start as Date).getTime();
          const rangeEnd = typeof timeRange.end === 'number' ? timeRange.end : (timeRange.end as Date).getTime();
          const inRange = ts >= rangeStart && ts <= rangeEnd;

          return {
            ...rel,
            disabled: !inRange,
          };
        })
      );
    }, [timeRange, inputNodes, inputEdges]);

    // Apply highlights
    useEffect(() => {
      const hasHighlights = highlightedNodes.size > 0 || highlightedEdges.size > 0;

      setNvlNodes((nodes) =>
        nodes.map((node) => ({
          ...node,
          highlighted: highlightedNodes.has(node.id),
          disabled: hasHighlights ? !highlightedNodes.has(node.id) : false,
        }))
      );

      setNvlRelationships((rels) =>
        rels.map((rel) => ({
          ...rel,
          highlighted: highlightedEdges.has(rel.id),
          disabled: hasHighlights ? !highlightedEdges.has(rel.id) : false,
        }))
      );
    }, [highlightedNodes, highlightedEdges]);


    // Handle node click
    const handleNodeClick = useCallback(
      (node: NVLNodeType, hitTargets: HitTargets, evt: MouseEvent) => {
        const originalNode = inputNodes.find((n) => n.id === node.id);
        if (originalNode) {
          onNodeClick?.(originalNode);
        }

        // Find connected edges and nodes for highlighting
        const connectedEdgeIds = new Set<string>();
        const connectedNodeIds = new Set<string>([node.id]);

        inputEdges.forEach((edge) => {
          if (edge.source === node.id || edge.target === node.id) {
            connectedEdgeIds.add(edge.id);
            connectedNodeIds.add(edge.source);
            connectedNodeIds.add(edge.target);
          }
        });

        setHighlightedNodes(connectedNodeIds);
        setHighlightedEdges(connectedEdgeIds);
        setSelectedNodes([node.id]);
        onSelectionChange?.([node.id], []);
      },
      [inputNodes, inputEdges, onNodeClick, onSelectionChange]
    );

    // Handle relationship click
    const handleRelationshipClick = useCallback(
      (rel: NVLRelType, hitTargets: HitTargets, evt: MouseEvent) => {
        const originalEdge = inputEdges.find((e) => e.id === rel.id);
        if (originalEdge) {
          onEdgeClick?.(originalEdge);
        }
        setSelectedEdges([rel.id]);
        onSelectionChange?.([], [rel.id]);
      },
      [inputEdges, onEdgeClick, onSelectionChange]
    );

    // Handle canvas click (clear highlights)
    const handleCanvasClick = useCallback(
      (evt: MouseEvent) => {
        setHighlightedNodes(new Set());
        setHighlightedEdges(new Set());
        setSelectedNodes([]);
        setSelectedEdges([]);
        onSelectionChange?.([], []);
      },
      [onSelectionChange]
    );

    // Mouse event callbacks for NVL
    const mouseEventCallbacks: MouseEventCallbacks = useMemo(
      () => ({
        onNodeClick: handleNodeClick,
        onRelationshipClick: handleRelationshipClick,
        onCanvasClick: handleCanvasClick,
        onPan: true, // Enable panning
        onZoom: true, // Enable zooming
        onDrag: true, // Enable node dragging
      }),
      [handleNodeClick, handleRelationshipClick, handleCanvasClick]
    );

    // Interaction options
    const interactionOptions = useMemo(() => ({
      excludeNodeMargin: false, // Allow panning even near nodes
    }), []);

    // Expose methods via ref
    useImperativeHandle(ref, () => ({
      fitView: () => {
        // Use NVL's fit method to fit all nodes in view
        if (nvlRef.current) {
          const nodeIds = inputNodes.map(n => n.id);
          nvlRef.current.fit(nodeIds);
        }
      },

      setLayout: (options: LayoutOptions) => {
        const nvlLayout = mapLayoutToNVL(options.type);
        setCurrentNvlLayout(nvlLayout);

        // Use NVL's native setLayout method
        if (nvlRef.current) {
          nvlRef.current.setLayout(nvlLayout);

          // Set layout options if applicable
          if (options.type === 'hierarchical' && options.direction) {
            const directionMap: Record<string, 'up' | 'down' | 'left' | 'right'> = {
              'TB': 'down',
              'BT': 'up',
              'LR': 'right',
              'RL': 'left',
            };
            nvlRef.current.setLayoutOptions({
              direction: directionMap[options.direction] || 'down',
            });
          }
        }
      },

      getMetrics: () => metrics,

      highlightPath: (sourceId: string, targetId: string) => {
        const path = shortestPath(inputNodes, inputEdges, sourceId, targetId);
        if (!path) return;

        const pathNodeSet = new Set(path);
        const pathEdgeSet = new Set<string>();

        for (let i = 0; i < path.length - 1; i++) {
          const edge = inputEdges.find(
            (e) =>
              (e.source === path[i] && e.target === path[i + 1]) ||
              (e.target === path[i] && e.source === path[i + 1])
          );
          if (edge) pathEdgeSet.add(edge.id);
        }

        setHighlightedNodes(pathNodeSet);
        setHighlightedEdges(pathEdgeSet);
      },

      clearHighlight: () => {
        setHighlightedNodes(new Set());
        setHighlightedEdges(new Set());
      },

      colorByCommunity: () => {
        const communities = labelPropagation(inputNodes, inputEdges);
        const colors = [
          '#6366f1', '#ec4899', '#10b981', '#f59e0b', '#8b5cf6',
          '#06b6d4', '#ef4444', '#84cc16', '#f97316', '#3b82f6',
        ];

        setNvlNodes((nodes) =>
          nodes.map((node) => ({
            ...node,
            color: colors[(communities.get(node.id) || 0) % colors.length],
          }))
        );
      },

      colorByMetric: (metricName: keyof SNAMetrics) => {
        const currentMetrics = metricsRef.current;
        if (!currentMetrics) {
          console.warn('colorByMetric: No metrics available');
          return;
        }
        const metricMap = currentMetrics[metricName];

        const values = Array.from(metricMap.values());
        const min = Math.min(...values);
        const max = Math.max(...values);
        const range = max - min || 1;

        setNvlNodes((nodes) =>
          nodes.map((node) => {
            const value = metricMap.get(node.id) || 0;
            const normalized = (value - min) / range;
            const hue = 250 - normalized * 150; // Purple to green
            return {
              ...node,
              color: `hsl(${hue}, 70%, 50%)`,
            };
          })
        );
      },

      exportImage: async () => {
        if (nvlRef.current) {
          return nvlRef.current.getImageDataUrl();
        }
        return 'data:image/png;base64,...';
      },

      getSelectedNodes: () => selectedNodes,
      getSelectedEdges: () => selectedEdges,

      scaleByConnections: (enabled: boolean) => {
        const degrees = calculateDegrees(inputEdges);
        const degreeValues = Array.from(degrees.values());
        const minDeg = Math.min(...degreeValues, 0);
        const maxDeg = Math.max(...degreeValues, 1);

        setNvlNodes((nodes) =>
          nodes.map((node) => ({
            ...node,
            size: enabled
              ? scaleNodeSize(
                  degrees.get(node.id) || 0,
                  minDeg,
                  maxDeg,
                  minNodeSize,
                  maxNodeSize
                )
              : originalNodeSizes.get(node.id) || 30,
          }))
        );
      },

      groupByAttribute: (attribute: string) => {
        const groups = new Map<string, string[]>();
        inputNodes.forEach((node) => {
          const value = String(node.data?.[attribute] ?? 'Other');
          if (!groups.has(value)) {
            groups.set(value, []);
          }
          groups.get(value)!.push(node.id);
        });

        const colors = [
          '#6366f1', '#ec4899', '#10b981', '#f59e0b', '#8b5cf6',
          '#06b6d4', '#ef4444', '#84cc16', '#f97316', '#3b82f6',
        ];

        const groupColors = new Map<string, string>();
        Array.from(groups.keys()).forEach((key, index) => {
          groupColors.set(key, colors[index % colors.length]);
        });

        setNvlNodes((nodes) =>
          nodes.map((node) => {
            const originalNode = inputNodes.find((n) => n.id === node.id);
            const value = String(originalNode?.data?.[attribute] ?? 'Other');
            return {
              ...node,
              color: groupColors.get(value),
            };
          })
        );
      },

      ungroupAll: () => {
        setNvlNodes((nodes) =>
          nodes.map((node) => {
            const originalNode = inputNodes.find((n) => n.id === node.id);
            return {
              ...node,
              color: originalNode?.color || '#6366f1',
            };
          })
        );
      },
    }), [inputNodes, inputEdges, minNodeSize, maxNodeSize, originalNodeSizes, selectedNodes, selectedEdges]);

    // Time items for TimeBar
    const timeItems = useMemo(() => {
      const items: Array<{ id: string; timestamp?: number | Date }> = [];
      inputNodes.forEach((n) => {
        if (n.timestamp) items.push({ id: n.id, timestamp: n.timestamp });
      });
      inputEdges.forEach((e) => {
        if (e.timestamp) items.push({ id: e.id, timestamp: e.timestamp });
      });
      return items;
    }, [inputNodes, inputEdges]);

    // Theme colors
    const bgColor = theme === 'dark' ? '#111827' : '#fafafa';

    // Map NVL nodes to the format expected by InteractiveNvlWrapper
    const mappedNodes = useMemo(() =>
      nvlNodes.map((node) => ({
        id: node.id,
        size: node.size,
        color: node.disabled ? '#cccccc' : (node.highlighted ? '#fbbf24' : node.color),
        caption: node.caption,
        icon: node.icon,
      })) as NVLNodeType[],
      [nvlNodes]
    );

    const mappedRelationships = useMemo(() =>
      nvlRelationships.map((rel) => ({
        id: rel.id,
        from: rel.from,
        to: rel.to,
        caption: rel.caption,
        color: rel.disabled ? '#cccccc' : (rel.highlighted ? '#fbbf24' : rel.color),
        width: rel.width,
      })) as NVLRelType[],
      [nvlRelationships]
    );

    // NVL options
    const nvlOptions = useMemo(() => ({
      allowDynamicMinZoom: true,
      minZoom: 0.1,
      maxZoom: 2,
      renderer: 'canvas' as const, // Use canvas for captions
      disableWebWorkers: true, // Disable web workers to avoid Vite bundling issues
    }), []);

    // Layout options - configured for better edge crossing avoidance
    const layoutOptions = useMemo(() => {
      if (currentNvlLayout === 'hierarchical') {
        return {
          direction: 'down' as const,
          packing: 'bin' as const, // Better node packing to reduce crossings
        };
      }
      if (currentNvlLayout === 'forceDirected') {
        return {
          enableCytoscape: true, // Use CoseBilkent for small graphs (better layout quality)
        };
      }
      return undefined;
    }, [currentNvlLayout]);

    // Ref to track input nodes for callbacks (avoids dependency issues)
    const inputNodesRef = useRef(inputNodes);
    useEffect(() => {
      inputNodesRef.current = inputNodes;
    }, [inputNodes]);

    // Callback when layout is done - fit view (only once per layout change)
    const nvlCallbacks = useMemo(() => ({
      onLayoutDone: () => {
        // Auto fit after initial layout completes
        if (nvlRef.current && !hasInitialFit.current) {
          hasInitialFit.current = true;
          const nodeIds = inputNodesRef.current.map(n => n.id);
          setTimeout(() => {
            nvlRef.current?.fit(nodeIds);
          }, 100);
        }
      },
    }), []); // No dependencies - stable callback

    return (
      <div
        className={className}
        style={{
          width: '100%',
          height: '100%',
          display: 'flex',
          flexDirection: 'column',
          background: bgColor,
          ...style,
        }}
      >
        <div style={{ flex: 1, position: 'relative', minHeight: 400 }}>
          <InteractiveNvlWrapper
            ref={nvlRef as any}
            nodes={mappedNodes}
            rels={mappedRelationships}
            layout={currentNvlLayout}
            layoutOptions={layoutOptions}
            nvlOptions={nvlOptions}
            nvlCallbacks={nvlCallbacks}
            mouseEventCallbacks={mouseEventCallbacks}
            interactionOptions={interactionOptions}
          />

          {/* Stats panel */}
          {metrics && (
            <div
              style={{
                position: 'absolute',
                top: 12,
                right: 12,
                padding: 12,
                background: theme === 'dark' ? '#1f2937' : 'white',
                borderRadius: 8,
                fontSize: 11,
                boxShadow: '0 2px 8px rgba(0, 0, 0, 0.1)',
                color: theme === 'dark' ? '#e5e7eb' : '#374151',
              }}
            >
              <div style={{ fontWeight: 600, marginBottom: 8 }}>Graph Stats</div>
              <div>Nodes: {inputNodes.length}</div>
              <div>Edges: {inputEdges.length}</div>
              <div>
                Density:{' '}
                {(
                  (2 * inputEdges.length) /
                  (inputNodes.length * (inputNodes.length - 1) || 1)
                ).toFixed(3)}
              </div>
            </div>
          )}
        </div>

        {/* Time bar */}
        {showTimeBar && timeItems.length > 0 && (
          <div style={{ padding: 16 }}>
            <TimeBar
              items={timeItems}
              selectedRange={
                timeRange
                  ? { start: timeRange.start as number, end: timeRange.end as number }
                  : undefined
              }
              onChange={(range) =>
                setTimeRange({ start: range.start, end: range.end })
              }
              theme={theme}
            />
          </div>
        )}
      </div>
    );
  }
);

Chart.displayName = 'Chart';

export default Chart;
