import { memo, useMemo } from 'react';
import { type EdgeProps } from '@xyflow/react';
import type { NodeConnectionDto } from '../../engine-contract';
import { getEdgeStyle } from './edgeStyleMap';
import './WorkflowEdge.css';

function buildCubicBezierPath(
  sx: number, sy: number,
  tx: number, ty: number,
): string {
  const vdy = Math.abs(ty - sy);
  const offset = Math.max(40, Math.min(vdy * 0.6, 120));
  return `M ${sx} ${sy} C ${sx} ${sy + offset}, ${tx} ${ty - offset}, ${tx} ${ty}`;
}

function WorkflowEdgeInner(props: EdgeProps) {
  const { sourceX, sourceY, targetX, targetY, data, selected } = props;
  const dto = (data as unknown as { dto: NodeConnectionDto })?.dto;
  const msgType = dto?.triggerMessageType ?? 'Complete';
  const style = getEdgeStyle(msgType);

  const edgePath = useMemo(
    () => buildCubicBezierPath(sourceX, sourceY, targetX, targetY),
    [sourceX, sourceY, targetX, targetY],
  );

  const classes = [
    'workflow-edge-path',
    style.cssClass,
    selected ? 'selected' : '',
  ].filter(Boolean).join(' ');

  const markerId = style.cssClass === 'error-edge' ? 'url(#arrow-err)' : 'url(#arrow)';

  return (
    <>
      {/* Arrow marker definitions */}
      <defs>
        <marker id="arrow" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto">
          <path d="M 0 0 L 10 5 L 0 10 z" fill="var(--stroke-3)" />
        </marker>
        <marker id="arrow-err" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="6" markerHeight="6" orient="auto">
          <path d="M 0 0 L 10 5 L 0 10 z" fill="var(--err)" />
        </marker>
      </defs>
      <path
        className={classes}
        d={edgePath}
        markerEnd={markerId}
        style={{
          stroke: style.cssColor,
          strokeWidth: style.strokeWidth,
          strokeDasharray: style.dashArray,
        }}
      />
      {/* Edge label at midpoint */}
      {msgType !== 'Complete' && (
        <>
          <rect
            className="edge-label-bg"
            x={(sourceX + targetX) / 2 - style.label.length * 3 - 5}
            y={(sourceY + targetY) / 2 - 8}
            width={style.label.length * 6 + 10}
            height={16}
            rx={8}
          />
          <text
            className="edge-label-text"
            x={(sourceX + targetX) / 2}
            y={(sourceY + targetY) / 2}
            textAnchor="middle"
            dominantBaseline="middle"
          >
            {style.label}
          </text>
        </>
      )}
    </>
  );
}

export const WorkflowEdge = memo(WorkflowEdgeInner);