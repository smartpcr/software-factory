import { memo } from 'react';
import { Handle, Position, type NodeProps } from '@xyflow/react';
import type { NodeDto, NodeExecutionStatus } from '../../engine-contract';
import { NodeChrome } from './NodeChrome';
import { useGraphStore } from '../state/graphStore';

export interface WorkflowNodeData {
  dto: NodeDto;
}

function WorkflowNodeInner({ data, selected, id }: NodeProps) {
  const nodeData = data as unknown as WorkflowNodeData;
  const dto = nodeData.dto;
  const executionStatuses = useGraphStore((s) => s.executionStatuses);
  const status: NodeExecutionStatus | undefined = executionStatuses.get(id);

  return (
    <>
      <Handle type="target" position={Position.Top} />
      <NodeChrome
        runtimeType={dto.runtimeType}
        nodeId={dto.nodeId}
        nodeName={dto.nodeName}
        selected={selected}
        status={status}
      />
      <Handle type="source" position={Position.Bottom} />
    </>
  );
}

export const WorkflowNode = memo(WorkflowNodeInner);