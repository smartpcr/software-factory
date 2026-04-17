import React from 'react';
import type { RuntimeType } from '../../engine-contract';
import { getNodeIcon } from '../theme/nodeIcons';

export interface NodeChromeProps {
  runtimeType: RuntimeType;
  nodeId: string;
  nodeName: string;
  selected?: boolean;
  children?: React.ReactNode;
}

export function NodeChrome({
  runtimeType,
  nodeId,
  nodeName,
  selected,
  children,
}: NodeChromeProps) {
  const icon = getNodeIcon(runtimeType);

  return (
    <div
      data-testid={`node-chrome-${nodeId}`}
      aria-label={`${icon.label} node: ${nodeName || nodeId}`}
      style={{
        border: `2px solid ${selected ? '#3b82f6' : icon.color}`,
        borderRadius: 8,
        backgroundColor: '#fff',
        padding: 8,
        minWidth: 160,
        boxShadow: selected ? '0 0 0 2px #3b82f6' : '0 1px 3px rgba(0,0,0,0.1)',
      }}
    >
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 6,
          marginBottom: 4,
          fontWeight: 600,
          fontSize: 13,
          color: icon.color,
        }}
      >
        <span aria-hidden="true">{icon.emoji}</span>
        <span>{icon.label}</span>
      </div>
      <div style={{ fontSize: 12, color: '#374151' }}>{nodeName || nodeId}</div>
      {children}
    </div>
  );
}