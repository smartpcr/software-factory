import { MiniMap } from '@xyflow/react';
import type { RuntimeType } from '../../engine-contract';
import { NODE_ICONS } from '../theme/nodeIcons';

function nodeColor(node: { data?: { dto?: { runtimeType?: string } } }): string {
  const rt = node.data?.dto?.runtimeType as RuntimeType | undefined;
  if (rt && NODE_ICONS[rt]) return NODE_ICONS[rt].color;
  return '#94a3b8';
}

export function WorkflowMiniMap() {
  return (
    <MiniMap
      nodeColor={nodeColor}
      data-testid="workflow-minimap"
      style={{ border: '1px solid #e5e7eb', borderRadius: 4 }}
    />
  );
}