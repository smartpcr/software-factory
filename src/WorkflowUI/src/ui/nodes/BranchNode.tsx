import { memo } from 'react';
import { Handle, Position, type NodeProps } from '@xyflow/react';
import type { NodeDto, NodeExecutionStatus } from '../../engine-contract';
import { getNodeIcon } from '../theme/nodeIcons';
import { NodeBodyPreview } from './NodeBodyPreview';
import { NodeChrome } from './NodeChrome';
import { SiblingConnector } from './SiblingConnector';
import { GapDropSlot } from './GapDropSlot';
import { useGraphStore, type SidecarNodeMeta } from '../state/graphStore';
import './BranchNode.css';

export interface BranchNodeData {
  dto: NodeDto;
}

const BRANCH_MAP: Record<string, string[]> = {
  IfElse: ['true', 'false'],
  Switch: ['case_a', 'case_b', 'default'],
  ForEach: ['iterate', 'complete'],
  While: ['iterate', 'complete'],
};

function BranchNodeInner({ data, selected, id }: NodeProps) {
  const nodeData = data as unknown as BranchNodeData;
  const dto = nodeData.dto;
  const executionStatuses = useGraphStore((s) => s.executionStatuses);
  const status: NodeExecutionStatus | undefined = executionStatuses.get(id);
  const sidecarMeta = useGraphStore((s) => s.sidecarMeta);
  const nodes = useGraphStore((s) => s.nodes);
  const icon = getNodeIcon(dto.runtimeType);
  const IconComp = icon.Icon;

  const branches = BRANCH_MAP[dto.runtimeType] ?? [];
  const meta = sidecarMeta.get(id) as (SidecarNodeMeta & { branchChildren?: Record<string, string[]> }) | undefined;

  return (
    <>
      <Handle type="target" position={Position.Top} className="custom-port" />
      <div
        className={`node-chrome${selected ? ' selected' : ''}`}
        data-testid={`node-chrome-${id}`}
        data-status={status ?? ''}
        aria-label={`${icon.label} node: ${dto.nodeName || id}`}
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
          <span className="node-type-label">{icon.label}</span>
        </div>

        <NodeBodyPreview runtimeType={dto.runtimeType} />

        {/* Branch ports */}
        {branches.length > 0 && (
          <div className="branch-ports" data-testid="branch-ports">
            {branches.map((branch) => {
              const childIds = meta?.branchChildren?.[branch] ?? [];
              const childNodes = childIds
                .map((cid) => nodes.find((n) => n.nodeId === cid))
                .filter((n): n is NodeDto => n != null);

              return (
                <div
                  key={branch}
                  className="branch-port"
                  data-branch={branch}
                  data-parent-id={id}
                  data-testid={`branch-port-${branch}`}
                >
                  <div className="branch-port-label">{branch}</div>
                  <div className="branch-port-children">
                    {childNodes.length === 0 ? (
                      <div className="branch-port-empty">Drop component</div>
                    ) : (
                      <>
                        <GapDropSlot parentId={id} slot={branch} index={0} />
                        {childNodes.map((child, i) => (
                          <div key={child.nodeId}>
                            <NodeChrome
                              runtimeType={child.runtimeType}
                              nodeId={child.nodeId}
                              nodeName={child.nodeName}
                              status={executionStatuses.get(child.nodeId)}
                            />
                            {i < childNodes.length - 1 && <SiblingConnector />}
                            <GapDropSlot parentId={id} slot={branch} index={i + 1} />
                          </div>
                        ))}
                      </>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </div>
      {/* Branch output ports are handled via edges from the branch slots */}
      <Handle type="source" position={Position.Bottom} className="custom-port" />
    </>
  );
}

export const BranchNode = memo(BranchNodeInner);
