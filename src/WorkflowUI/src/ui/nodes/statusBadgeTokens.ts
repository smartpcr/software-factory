import type { NodeExecutionStatus } from '../../engine-contract';

export interface StatusBadgeToken {
  label: string;
  color: string;
  bgColor: string;
  icon: string;
}

export const STATUS_BADGE_TOKENS: Record<NodeExecutionStatus, StatusBadgeToken> = {
  Pending: { label: 'Pending', color: '#64748b', bgColor: '#f1f5f9', icon: '⏳' },
  Running: { label: 'Running', color: '#2563eb', bgColor: '#dbeafe', icon: '▶️' },
  Completed: { label: 'Completed', color: '#16a34a', bgColor: '#dcfce7', icon: '✅' },
  Failed: { label: 'Failed', color: '#dc2626', bgColor: '#fee2e2', icon: '❌' },
  Cancelled: { label: 'Cancelled', color: '#9333ea', bgColor: '#f3e8ff', icon: '🚫' },
  Skipped: { label: 'Skipped', color: '#71717a', bgColor: '#f4f4f5', icon: '⏭️' },
};

export function getStatusBadgeToken(status: NodeExecutionStatus): StatusBadgeToken {
  return STATUS_BADGE_TOKENS[status];
}