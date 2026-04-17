import type { NodeExecutionStatus } from '../../engine-contract';
import { getStatusBadgeToken } from './statusBadgeTokens';

export interface NodeStatusBadgeProps {
  status: NodeExecutionStatus;
}

export function NodeStatusBadge({ status }: NodeStatusBadgeProps) {
  const token = getStatusBadgeToken(status);
  return (
    <span
      data-testid="node-status-badge"
      aria-label={`Status: ${token.label}`}
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 4,
        padding: '2px 6px',
        borderRadius: 4,
        fontSize: 11,
        fontWeight: 600,
        color: token.color,
        backgroundColor: token.bgColor,
      }}
    >
      <span aria-hidden="true">{token.icon}</span>
      {token.label}
    </span>
  );
}