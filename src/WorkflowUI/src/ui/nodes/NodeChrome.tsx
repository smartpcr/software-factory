import type { RuntimeType, NodeExecutionStatus } from '../../engine-contract';
import { getNodeIcon } from '../theme/nodeIcons';
import { NodeBodyPreview } from './NodeBodyPreview';
import './NodeChrome.css';

export interface NodeChromeProps {
  runtimeType: RuntimeType;
  nodeId: string;
  nodeName: string;
  selected?: boolean;
  status?: NodeExecutionStatus;
  children?: React.ReactNode;
}

export function NodeChrome({
  runtimeType,
  nodeId,
  nodeName,
  selected,
  status,
  children,
}: NodeChromeProps) {
  const icon = getNodeIcon(runtimeType);
  const IconComp = icon.Icon;

  return (
    <div
      className={`node-chrome${selected ? ' selected' : ''}`}
      data-testid={`node-chrome-${nodeId}`}
      data-status={status ?? ''}
      aria-label={`${icon.label} node: ${nodeName || nodeId}`}
      style={{ color: icon.cssColor }}
    >
      {/* Status badge */}
      {status && (
        <span className="node-status-badge" data-testid="node-status-badge" aria-label={`Status: ${status}`}>
          {status}
        </span>
      )}

      {/* Header */}
      <div className="node-head">
        <div className="node-head-accent" />
        <div className="node-icon">
          <IconComp size={12} />
        </div>
        <div className="node-title">{nodeName || nodeId}</div>
        <span className="node-type-label">{icon.label}</span>
      </div>

      {/* Body preview */}
      <NodeBodyPreview runtimeType={runtimeType} />

      {children}
    </div>
  );
}