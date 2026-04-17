import { memo } from 'react';
import { BaseEdge, getBezierPath, type EdgeProps } from '@xyflow/react';
import type { NodeConnectionDto } from '../../engine-contract';
import { getEdgeStyle } from './edgeStyleMap';

function WorkflowEdgeInner(props: EdgeProps) {
  const { sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, data } = props;
  const dto = (data as unknown as { dto: NodeConnectionDto })?.dto;
  const style = dto ? getEdgeStyle(dto.triggerMessageType) : getEdgeStyle('Complete');

  const [edgePath] = getBezierPath({
    sourceX,
    sourceY,
    targetX,
    targetY,
    sourcePosition,
    targetPosition,
  });

  return (
    <BaseEdge
      path={edgePath}
      style={{
        stroke: style.color,
        strokeWidth: style.strokeWidth,
        strokeDasharray: style.dashArray,
      }}
    />
  );
}

export const WorkflowEdge = memo(WorkflowEdgeInner);