import type { NodeDto, NodeConnectionDto } from '../../engine-contract';
import { useGraphStore } from '../state/graphStore';
import { getNodeIcon } from '../theme/nodeIcons';
import { PropertyGrid } from '../property-grid/PropertyGrid';
import { EdgeInspector } from './EdgeInspector';
import './Inspector.css';

export interface InspectorProps {
  editingEdge?: NodeConnectionDto | null;
  onEdgeClose?: () => void;
}

export function Inspector({ editingEdge, onEdgeClose }: InspectorProps) {
  const nodes = useGraphStore((s) => s.nodes);
  const selectedNodeIds = useGraphStore((s) => s.selectedNodeIds);

  const firstSelectedId = Array.from(selectedNodeIds)[0] as string | undefined;
  const selectedNode: NodeDto | null =
    firstSelectedId
      ? (nodes.find((n) => n.nodeId === firstSelectedId) ?? null)
      : null;

  // If editing an edge, show edge inspector
  if (editingEdge && onEdgeClose) {
    return (
      <div className="inspector" data-testid="inspector">
        <EdgeInspector connection={editingEdge} onClose={onEdgeClose} />
      </div>
    );
  }

  // If no node selected, show empty state
  if (!selectedNode) {
    return (
      <div className="inspector" data-testid="inspector">
        <div className="ins-empty">
          <svg className="ins-empty-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
            <circle cx="12" cy="12" r="9" />
            <path d="M12 8v4l2 2" />
          </svg>
          <div>Select a node or edge<br />to configure its properties</div>
        </div>
      </div>
    );
  }

  // Node selected — show header + property grid
  const icon = getNodeIcon(selectedNode.runtimeType);
  const IconComp = icon.Icon;

  return (
    <div className="inspector" data-testid="inspector">
      <div className="ins-header">
        <div className="ins-title">
          <div className="ins-title-icon" style={{ color: icon.cssColor }}>
            <IconComp size={14} />
          </div>
          <div>
            <div className="ins-title-text">{selectedNode.nodeName || selectedNode.nodeId}</div>
            <div className="ins-subtitle">{icon.label}</div>
          </div>
        </div>
      </div>
      <PropertyGrid />
    </div>
  );
}
