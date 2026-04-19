import { memo, useCallback, useState } from 'react';
import { Handle, Position, type NodeProps } from '@xyflow/react';
import type { ContainerNodeDto, NodeDto, NodeExecutionStatus } from '../../engine-contract';
import { getNodeIcon } from '../theme/nodeIcons';
import { NodeChrome } from './NodeChrome';
import { SiblingConnector } from './SiblingConnector';
import { GapDropSlot } from './GapDropSlot';
import { useGraphStore, type SidecarNodeMeta } from '../state/graphStore';
import './ContainerNode.css';

export interface ContainerNodeData {
  dto: ContainerNodeDto;
}

function ContainerNodeInner({ data, selected, id }: NodeProps) {
  const nodeData = data as unknown as ContainerNodeData;
  const dto = nodeData.dto;
  const executionStatuses = useGraphStore((s) => s.executionStatuses);
  const status: NodeExecutionStatus | undefined = executionStatuses.get(id);
  const sidecarMeta = useGraphStore((s) => s.sidecarMeta);
  const nodes = useGraphStore((s) => s.nodes);
  const icon = getNodeIcon(dto.runtimeType);
  const IconComp = icon.Icon;

  const [collapsed, setCollapsed] = useState(false);

  const meta = sidecarMeta.get(id);
  const childIds: string[] = (meta as SidecarNodeMeta & { childIds?: string[] })?.childIds ?? [];

  const childNodes = childIds
    .map((cid) => nodes.find((n) => n.nodeId === cid))
    .filter((n): n is NodeDto => n != null);

  const toggleCollapse = useCallback((e: React.MouseEvent) => {
    e.stopPropagation();
    setCollapsed((c) => !c);
  }, []);

  return (
    <>
      <Handle type="target" position={Position.Top} className="custom-port" />
      <div
        className={`node-chrome container${selected ? ' selected' : ''}${collapsed ? ' collapsed' : ''}`}
        data-testid={`node-chrome-${id}`}
        data-status={status ?? ''}
        aria-label={`Container node: ${dto.nodeName || id}`}
        style={{ color: icon.cssColor }}
      >
        {status && (
          <span className="node-status-badge" data-testid="node-status-badge" aria-label={`Status: ${status}`}>
            {status}
          </span>
        )}

        <div className="node-head">
          <div className="node-head-accent" />
          <div className="node-icon">
            <IconComp size={12} />
          </div>
          <div className="node-title">{dto.nodeName || id}</div>
          <button className="collapse-btn" onClick={toggleCollapse} aria-label={collapsed ? 'Expand' : 'Collapse'}>
            <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <path d="M6 9l6 6 6-6" />
            </svg>
          </button>
          {collapsed && childNodes.length > 0 && (
            <span className="container-child-count">{childNodes.length}</span>
          )}
        </div>

        <div className="container-body" data-testid="container-body">
          <div className="container-children">
            {childNodes.length === 0 ? (
              <div className="container-hint">Drop component</div>
            ) : (
              <>
                <GapDropSlot parentId={id} slot="_body" index={0} />
                {childNodes.map((child, i) => (
                  <div key={child.nodeId}>
                    <NodeChrome
                      runtimeType={child.runtimeType}
                      nodeId={child.nodeId}
                      nodeName={child.nodeName}
                      status={executionStatuses.get(child.nodeId)}
                    />
                    {i < childNodes.length - 1 && <SiblingConnector />}
                    <GapDropSlot parentId={id} slot="_body" index={i + 1} />
                  </div>
                ))}
              </>
            )}
          </div>
        </div>
      </div>
      <Handle type="source" position={Position.Bottom} className="custom-port" />
    </>
  );
}

export const ContainerNode = memo(ContainerNodeInner);
